using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Abrupt stop during CTAS sort or copy sweeps temp files and reclaims the unpublished table (#1789)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenDatabaseAsync_TableAsAbruptStop_ShouldSweepReclaimAndRetry(bool copying)
    {
        // Arrange
        string root = Path.Combine(_root, "ctas-stop");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "FormatProbeFixture", "Assimalign.Cohesion.Database.Storage.FormatProbeFixture.dll"));
        start.ArgumentList.Add(copying ? "sql-ctas-copy-stop" : "sql-ctas-sort-stop"); start.ArgumentList.Add(root);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync(guard.Token);
        try
        {
            (await process.StandardOutput.ReadLineAsync(guard.Token)).ShouldBe("SQL_CTAS_READY");
            Directory.GetFiles(Path.Combine(root, ".cohesion-temp"), "*.ctmp").ShouldNotBeEmpty();
            // Act
            process.Kill(entireProcessTree: true); await process.WaitForExitAsync(guard.Token);
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); } }
        (await error).ShouldBeEmpty();
        var builder = SqlDatabaseEngine.CreateBuilder("ctas-stop-reopen");
        builder.Options.RootPath = root; builder.Options.StatementMemoryBudget = 262144;
        builder.Functions.Add(SqlScalarFunction.Create("probe", (long value) => value, volatility: SqlFunctionVolatility.Immutable));
        await using var engine = await builder.BuildAsync();
        var database = await engine.OpenDatabaseAsync("db", guard.Token);
        await using var session = await database.CreateSessionAsync(guard.Token);
        // Assert
        Directory.GetFiles(Path.Combine(root, ".cohesion-temp"), "*.ctmp").ShouldBeEmpty();
        database.Catalog.TryGetTable("dbo", "archive", out _).ShouldBeFalse();
        await ReclaimTableAsAsync(database, guard.Token); database.Catalog.GetPendingTableReclamations().ShouldBeEmpty();
        database.IsOffline.ShouldBeFalse();
        await using var source = (DatabaseResultSet)await session.ExecuteAsync("SELECT COUNT(*) FROM t", cancellationToken: guard.Token);
        (await Rows(source)).ShouldHaveSingleItem().Cells[0].ShouldBe(4000L);
        (await session.ExecuteAsync("CREATE TABLE archive AS SELECT * FROM t ORDER BY id DESC", cancellationToken: guard.Token)).AffectedCount.ShouldBe(4000);
        TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - CTAS spill memory: copying stays within the grant plus 2 MiB and releases drained rows (#1789)")]
    public async Task ExecuteAsync_TableAsLiveCopy_ShouldReleaseDrainedRowsWithinBound()
    {
        // Arrange: include the growing database pool/transaction pages in the fixed allowance.
        const long grant = 1048576, constant = 2097152;
        bool observe = false; long baseline = 0, peak = 0; int copies = 0;
        var weak = new List<WeakReference>(); var rows = new List<WeakReference>(); var vectors = new List<WeakReference>();
        var builder = SqlDatabaseEngine.CreateBuilder("ctas-live");
        builder.Options.RootPath = Path.Combine(_root, "ctas-live");
        builder.Options.StatementMemoryBudget = grant; builder.Options.StatementTempSpaceQuota = 67108864;
        builder.Functions.Add(SqlScalarFunction.Create("fresh", (long value) =>
        {
            string result = new((char)('a' + value % 23), 512);
            if (observe && value < 100) { weak.Add(new WeakReference(result)); }
            return result;
        }));
        await using var engine = await builder.BuildAsync();
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync(); await Seed(session);
        // Warm the same copy footprint so pool growth does not dominate the forced-GC sample.
        await session.ExecuteAsync("CREATE TABLE warm AS SELECT fresh(id) AS value FROM t ORDER BY id");
        baseline = GC.GetTotalMemory(true); observe = true;
        SqlPlanExecutor.TableAsSourceReading = (_, row) =>
        {
            if (rows.Count >= 100) { return; }
            rows.Add(new WeakReference(row)); vectors.Add(new WeakReference(TableAsValues((SqlMaterializedRow)row)));
        };
        SqlPlanExecutor.TableAsStage = (phase, _) =>
        {
            if (phase != "Copied") { return; }
            copies++;
            if (copies % 7 != 0) { return; }
            peak = Math.Max(peak, Math.Max(0, GC.GetTotalMemory(true) - baseline));
            weak.Count.ShouldBe(100); weak.Count(row => row.IsAlive).ShouldBe(0);
            rows.Count.ShouldBe(100); rows.Count(row => row.IsAlive).ShouldBe(0);
            vectors.Count.ShouldBe(100); vectors.Count(row => row.IsAlive).ShouldBe(0);
        };
        try
        {
            // Act
            (await session.ExecuteAsync("CREATE TABLE archive AS SELECT fresh(id) AS value FROM t ORDER BY id")).AffectedCount.ShouldBe(4000);
            // Assert
            copies.ShouldBe(63); SqlSortSpillMetrics.For(session.LastStatementMetrics!).ShouldNotBeNull();
            peak.ShouldBeLessThanOrEqualTo(grant + constant);
            _output.WriteLine($"CTAS_LIVE|copies={copies}|peak={peak}|grant={grant}|constant={constant}|weak={weak.Count}|alive={weak.Count(row => row.IsAlive)}|weakRows={rows.Count}|aliveRows={rows.Count(row => row.IsAlive)}|weakVectors={vectors.Count}|aliveVectors={vectors.Count(row => row.IsAlive)}");
            TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        }
        finally { SqlPlanExecutor.TableAsStage = null; SqlPlanExecutor.TableAsSourceReading = null; }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - CTAS spill brackets wait through checkpoint admission and copy each row once (#1283, #1789)")]
    public async Task ExecuteAsync_TableAsCheckpointPause_ShouldResumeWithoutRepeatingRows()
    {
        // Arrange: open a counted admission pause only after the first copied bracket.
        using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var engine = await Create("ctas-pause", 262144);
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync(); await Seed(session);
        var gate = TransactionCheckpointInterlock.ForStorage(database.DataStorage);
        var source = EventSource.GetSources().Single(s => s.Name == "Assimalign.Cohesion.Database.Transactions");
        using var recorder = new EventSourceRecorder(source, EventLevel.Verbose);
        StorageTransaction? outside = null; int copied = 0;
        SqlPlanExecutor.TableAsStage = (phase, _) =>
        {
            if (phase != "Copied") { return; }
            copied++;
            if (copied != 1) { return; }
            database.DataStorage.CheckpointJournalSize = 1; gate.JournalSizeLimit = 2;
            outside = database.DataStorage.BeginTransaction("HeldCtasCheckpoint");
            gate.ObserveAdmissionPause().ShouldBeTrue();
        };
        Task<DatabaseResult>? executing = null;
        try
        {
            // Act
            executing = Task.Run(async () => await session.ExecuteAsync("CREATE TABLE archive AS SELECT id,k FROM t ORDER BY id DESC", cancellationToken: guard.Token), guard.Token);
            while (!recorder.Events.Any(e => e.EventId == 25)) { await Task.Delay(1, guard.Token); }
            gate.IsAdmissionPaused.ShouldBeTrue(); copied.ShouldBe(1);
            database.Catalog.TryGetTable("dbo", "archive", out _).ShouldBeFalse();
            gate.JournalSizeLimit = long.MaxValue; // retain this pause; prevent a second episode
            outside.ShouldNotBeNull().Rollback(); outside = null;
            database.Coordinator.Checkpoint(guard.Token);
            (await executing).AffectedCount.ShouldBe(4000);
            // Assert
            copied.ShouldBe(63); gate.IsAdmissionPaused.ShouldBeFalse();
            (await TableAsPhysicalRowsAsync(session, "archive")).Select(row => row.Cells[0]).ShouldBe(Enumerable.Range(0, 4000).Reverse().Cast<object?>());
            TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        }
        finally
        {
            SqlPlanExecutor.TableAsStage = null; gate.JournalSizeLimit = 0;
            database.DataStorage.CheckpointJournalSize = 65536; outside?.Rollback();
            if (executing is not null) { try { await executing; } catch (OperationCanceledException) { } }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - CTAS spill crash images converge to absent or complete at every existing durable boundary (#1789)")]
    public async Task OpenDatabaseAsync_TableAsSpilledCrashImages_ShouldConvergeExactly()
    {
        // Arrange: 180 wide rows force a sort spill but only three copy brackets.
        using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var strategy = new CrashCaptureSqlStorageStrategy();
        string temp = Path.Combine(_root, "ctas-crash-temp");
        CrashCaptureSqlStorageStrategy.CrashPointRecorder recorder;
        CrashCaptureSqlStorageStrategy? sorting = null; ulong owner = 0; int copied = 0;
        await using (var engine = SqlDatabaseEngine.Create("ctas-crash", new() { StorageStrategy = strategy, TempDirectory = temp, StatementMemoryBudget = 262144, CheckpointJournalSize = 0 }))
        {
            var database = await engine.CreateDatabaseAsync("db");
            await using var session = await database.CreateSessionAsync();
            await session.ExecuteAsync("CREATE TABLE t(id INT,note TEXT)");
            await session.ExecuteAsync("INSERT INTO t VALUES" + string.Join(',', Enumerable.Range(0, 180).Select(i => $"({i},'{new string('x', 1100)}')")));
            SqlRowCollector.ObserveSpill = phase => { if (phase == "arena") { sorting = strategy.CaptureDurableImages(); } };
            SqlPlanExecutor.TableAsStage = (phase, id) =>
            {
                owner = id;
                if (phase != "Copied") { return; }
                copied++; database.Coordinator.Checkpoint(guard.Token); database.CatalogStorage.Checkpoint();
            };
            try
            {
                // Act
                using (recorder = strategy.RecordCrashPoints())
                { (await session.ExecuteAsync("CREATE TABLE archive AS SELECT * FROM t ORDER BY id DESC", cancellationToken: guard.Token)).AffectedCount.ShouldBe(180); }
            }
            finally { SqlRowCollector.ObserveSpill = null; SqlPlanExecutor.TableAsStage = null; }
            TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        }
        // Assert: the sort adds no durable state. Every write/flush/length image of the
        // existing reservation, copy, builder commit and publication is replayed below.
        sorting.ShouldNotBeNull(); copied.ShouldBe(3); int absent = 0, complete = 0;
        for (int point = -1; point < recorder.Count; point++)
        {
            await using var reopened = SqlDatabaseEngine.Create("ctas-crash-reopen", new() { StorageStrategy = point < 0 ? sorting : recorder.Open(point) });
            var database = await reopened.OpenDatabaseAsync("db", guard.Token);
            await using var session = await database.CreateSessionAsync(guard.Token);
            if (database.Catalog.TryGetTable("dbo", "archive", out _))
            {
                complete++;
                await using var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT id,note FROM archive", cancellationToken: guard.Token);
                var rows = await Rows(result, guard.Token);
                rows.Select(row => row.Cells[0]).ShouldBe(Enumerable.Range(0, 180).Reverse().Cast<object?>());
                rows.ShouldAllBe(row => Equals(row.Cells[1], new string('x', 1100)));
            }
            else
            {
                absent++; await ReclaimTableAsAsync(database, guard.Token); database.DataStorage.HasOwnerPages(owner).ShouldBeFalse();
            }
            database.IsOffline.ShouldBeFalse();
        }
        absent.ShouldBeGreaterThan(0); complete.ShouldBeGreaterThan(0);
        _output.WriteLine($"CTAS_CRASH|images={recorder.Count + 1}|absent={absent}|complete={complete}|brackets={copied}");
    }
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_values")]
    private static extern ref object?[] TableAsValues(SqlMaterializedRow row);
}
