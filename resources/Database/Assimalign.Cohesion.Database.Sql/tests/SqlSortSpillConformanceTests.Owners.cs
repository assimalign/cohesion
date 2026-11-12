using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Abrupt stop with a live join result and retired or building index owner reopens cleanly (#1789, #1808)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenAsync_JoinAndOwnerAbruptStop_ShouldSweepAndRecover(bool building)
    {
        // Arrange: both G=262,144 B and Q=67,108,864 B are explicit in the fixture.
        string root = Path.Combine(_root, "owner-abrupt");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "FormatProbeFixture", "Assimalign.Cohesion.Database.Storage.FormatProbeFixture.dll"));
        start.ArgumentList.Add("sql-join-owner-stop"); start.ArgumentList.Add(root); start.ArgumentList.Add(building ? "build" : "drop");
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync(guard.Token);
        try
        {
            (await process.StandardOutput.ReadLineAsync(guard.Token)).ShouldBe("SQL_JOIN_OWNER_READY");
            Directory.GetFiles(Path.Combine(root, ".cohesion-temp"), "*.ctmp").ShouldNotBeEmpty();
            // Act
            process.Kill(entireProcessTree: true); await process.WaitForExitAsync(guard.Token);
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); } }
        (await errors).ShouldBeEmpty();
        await using var engine = SqlDatabaseEngine.Create("join-owner-abrupt-reopen", new() { RootPath = root, StatementMemoryBudget = 262144, StatementTempSpaceQuota = 67108864, CheckpointJournalSize = 65536 });
        var database = await engine.OpenDatabaseAsync("db", guard.Token);
        while (database.TableReclamation.HasWork) { database.TableReclamation.RunBatch(guard.Token); await Task.Delay(1, guard.Token); }
        // Assert
        Directory.GetFiles(Path.Combine(root, ".cohesion-temp"), "*.ctmp").ShouldBeEmpty();
        database.Catalog.GetPendingTableReclamations().ShouldBeEmpty();
        database.IndexManager.ExportRegistrations().ShouldNotContain(index => index.Definition.Name == "building");
        database.IndexManager.ExportRegistrations().Count(index => index.Definition.Name == "retiring").ShouldBe(building ? 1 : 0);
        await using var session = await database.CreateSessionAsync(guard.Token);
        await using var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT COUNT(*) FROM stable", cancellationToken: guard.Token);
        (await Rows(result, guard.Token)).Single().Cells[0].ShouldBe(512L);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Join temp runs and retired or building index owners remain disjoint across crash images (#1789, #1808)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_JoinWithIndexOwner_ShouldIsolateAndRecover(bool building)
    {
        // Arrange: G=262,144 B, Q=67,108,864 B; the index belongs to another table.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var storage = new CrashCaptureSqlStorageStrategy();
        var builder = SqlDatabaseEngine.CreateBuilder("join-owner-seam");
        builder.Options.StorageStrategy = storage;
        builder.Options.TempDirectory = Path.Combine(_root, ".owner-temp");
        builder.Options.StatementMemoryBudget = 262144;
        builder.Options.StatementTempSpaceQuota = 67108864;
        await using var engine = await builder.BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("db", guard.Token);
        await using var session = await database.CreateSessionAsync(guard.Token);
        await SeedHashJoin(session);
        await session.ExecuteAsync("CREATE TABLE stable(id INT PRIMARY KEY,v INT)", cancellationToken: guard.Token);
        await session.ExecuteAsync("INSERT INTO stable VALUES" + string.Join(',', Enumerable.Range(0, 512).Select(i => $"({i},{i})")), cancellationToken: guard.Token);
        await session.ExecuteAsync("CREATE INDEX retiring ON stable(v)", cancellationToken: guard.Token);
        var registration = database.IndexManager.ExportRegistrations().Single(index => index.Definition.Name == "retiring");
        ulong retired = database.IndexManager.PageOwner(registration);
        var pages = database.DataStorage.GetOwnerIndexPages(retired).ToArray();
        var survivor = database.IndexManager.ExportRegistrations().Single(index => index.Definition.Name != "retiring");
        ulong survivorOwner = database.IndexManager.PageOwner(survivor);
        var survivorPages = database.DataStorage.GetOwnerIndexPages(survivorOwner).ToArray();
        var pin = await database.Coordinator.BeginSnapshotPinAsync(guard.Token);
        using var recorder = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.Informational);
        using var releaseBuild = new ManualResetEventSlim();
        var buildEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? build = null;
        CrashCaptureSqlStorageStrategy? image = null;
        ulong newOwner = 0;
        if (building)
        {
            SqlPlanExecutor.IndexBuildBoundary = (phase, _, _) =>
            {
                if (phase != "root-committed") { return; }
                var current = database.IndexManager.ExportRegistrations(includePrepared: true).Single(index => index.Definition.Name == "building");
                newOwner = database.IndexManager.PageOwner(current);
                buildEntered.TrySetResult(); releaseBuild.Wait(guard.Token);
            };
            build = Task.Run(async () =>
            {
                await using var writer = await database.CreateSessionAsync(guard.Token);
                await writer.ExecuteAsync("CREATE INDEX building ON stable(v)", cancellationToken: guard.Token);
            }, guard.Token);
            await buildEntered.Task.WaitAsync(guard.Token);
        }
        else
        {
            await session.ExecuteAsync("DROP INDEX retiring ON stable", cancellationToken: guard.Token);
            database.TableReclamation.RunBatch(guard.Token);
            database.DataStorage.GetOwnerIndexPages(retired).ShouldBe(pages);
        }
        var owners = database.IndexManager.ExportRegistrations(includePrepared: true)
            .Select(database.IndexManager.PageOwner).Append(retired).Distinct().Order().ToArray();
        var directory = owners.Select(owner => (Owner: owner, Pages: database.DataStorage.GetOwnerIndexPages(owner).ToArray())).ToArray();
        long totalPages = database.DataStorage.FreeSpaceMap.TotalPageCount;
        long freePages = database.DataStorage.FreeSpaceMap.FreePageCount;
        var contents = directory.SelectMany(entry => entry.Pages).ToDictionary(page => page, page =>
        {
            using var handle = database.DataStorage.PageManager.GetPage(page);
            return handle.Page.AsBodySpan().ToArray();
        });
        bool spilled = false;
        SqlPlanExecutor.ObserveHashSpill = (phase, _) =>
        {
            if (phase != "join-partitioned") { return; }
            spilled = true;
            Statements(engine.TempSpace!).ShouldHaveSingleItem(); TempBytes(engine.TempSpace!).ShouldBeGreaterThan(0);
            database.DataStorage.FreeSpaceMap.TotalPageCount.ShouldBe(totalPages);
            database.DataStorage.FreeSpaceMap.FreePageCount.ShouldBe(freePages);
            foreach (var entry in directory)
            {
                database.DataStorage.GetOwnerIndexPages(entry.Owner).ShouldBe(entry.Pages);
                foreach (var page in entry.Pages) { database.DataStorage.FreeSpaceMap.IsAllocated(page).ShouldBeTrue(); }
            }
            foreach (var entry in contents)
            {
                using var handle = database.DataStorage.PageManager.GetPage(entry.Key);
                handle.Page.AsBodySpan().ToArray().ShouldBe(entry.Value);
            }
            // These are durable bytes captured while both independent owners are live,
            // without checkpoint/disposal: reopening exercises abrupt-stop recovery.
            image = storage.CaptureDurableImages();
        };
        try
        {
            // Act
            await using (var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT l.id,r.id FROM jl l FULL JOIN jr r ON l.id=r.id LIMIT 64", cancellationToken: guard.Token))
            { (await Rows(result, guard.Token)).Length.ShouldBe(64); }

            // Assert: temp cleanup cannot change even a prepared/retired owner directory.
            spilled.ShouldBeTrue(); image.ShouldNotBeNull();
            TempBytes(engine.TempSpace!).ShouldBe(0); Statements(engine.TempSpace!).ShouldBeEmpty(); Files(engine).ShouldBe(0);
            database.DataStorage.FreeSpaceMap.TotalPageCount.ShouldBe(totalPages);
            database.DataStorage.FreeSpaceMap.FreePageCount.ShouldBe(freePages);
            foreach (var entry in directory) { database.DataStorage.GetOwnerIndexPages(entry.Owner).ShouldBe(entry.Pages); }
            foreach (var entry in contents)
            {
                using var handle = database.DataStorage.PageManager.GetPage(entry.Key);
                handle.Page.AsBodySpan().ToArray().ShouldBe(entry.Value);
            }
        }
        finally
        {
            SqlPlanExecutor.ObserveHashSpill = null; releaseBuild.Set();
            if (build is not null) { await build; }
            SqlPlanExecutor.IndexBuildBoundary = null;
            await database.Coordinator.RollbackAsync(pin, guard.Token);
        }
        while (database.TableReclamation.HasWork) { database.TableReclamation.RunBatch(guard.Token); await Task.Delay(1, guard.Token); }
        if (!building)
        {
            recorder.Events.Where(entry => entry.EventId == 30 && Equals(entry.Payload![0], engine.Name.ToString()) && Equals(entry.Payload![2], (long)retired))
                .Sum(entry => (int)entry.Payload![3]!).ShouldBe(pages.Length);
            foreach (var page in pages) { database.DataStorage.FreeSpaceMap.IsAllocated(page).ShouldBeFalse(); }
        }
        else { newOwner.ShouldNotBe(0UL); database.DataStorage.GetOwnerIndexPages(newOwner).ShouldNotBeEmpty(); }
        database.DataStorage.GetOwnerIndexPages(survivorOwner).ShouldBe(survivorPages);

        var reopening = SqlDatabaseEngine.CreateBuilder("join-owner-reopen");
        reopening.Options.StorageStrategy = image!;
        await using var reopened = await reopening.BuildAsync(guard.Token);
        var recovered = await reopened.OpenDatabaseAsync("db", guard.Token);
        while (recovered.TableReclamation.HasWork) { recovered.TableReclamation.RunBatch(guard.Token); await Task.Delay(1, guard.Token); }
        recovered.DataStorage.GetOwnerIndexPages(survivorOwner).ShouldBe(survivorPages);
        recovered.Catalog.GetPendingTableReclamations().ShouldBeEmpty();
        if (!building)
        {
            recovered.DataStorage.GetOwnerIndexPages(retired).ShouldBeEmpty();
            foreach (var page in pages) { recovered.DataStorage.FreeSpaceMap.IsAllocated(page).ShouldBeFalse(); }
        }
        else { recovered.DataStorage.GetOwnerIndexPages(newOwner).ShouldBeEmpty(); }
        await using var reader = await recovered.CreateSessionAsync(guard.Token);
        await using var rows = (DatabaseResultSet)await reader.ExecuteAsync("SELECT COUNT(*) FROM stable", cancellationToken: guard.Token);
        (await Rows(rows, guard.Token)).Single().Cells[0].ShouldBe(512L);
        _output.WriteLine($"JOIN_OWNER_SEAM|building={building}|retired_pages={pages.Length}|survivor_pages={survivorPages.Length}|owners={directory.Length}|temp_cleaned=true|reopened_rows=512");
    }
}
