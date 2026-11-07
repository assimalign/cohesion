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

    [Theory(DisplayName = "Cohesion Test [Sql] - CTAS copy has fixed pool growth and a row-count independent live bound; retained-row mutant fails (#1789)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_TableAsLiveCopy_ShouldReleaseDrainedRowsWithinBound(bool mutant)
    {
        // Arrange: a warmed minimum pool excludes destination page growth from the sample.
        const long grant = 1048576, constant = 553240;
        var peaks = new List<long>();
        foreach (int count in new[] { 4000, 16000 })
        {
            var builder = SqlDatabaseEngine.CreateBuilder("ctas-live");
            builder.Options.RootPath = Path.Combine(_root, "live-" + count);
            builder.Options.BufferPoolCapacity = 1048576;
            builder.Options.StatementMemoryBudget = grant; builder.Options.StatementTempSpaceQuota = 67108864;
            builder.Functions.Add(SqlScalarFunction.Create("fresh", (long value) => new string((char)('a' + value % 23), 512), volatility: SqlFunctionVolatility.Immutable));
            await using var engine = await builder.BuildAsync();
            var database = await engine.CreateDatabaseAsync("db");
            await using var session = await database.CreateSessionAsync(); await Seed(session, count);
            const string query = "SELECT id,fresh(id) AS value FROM t ORDER BY id";
            await session.ExecuteAsync("CREATE TABLE warm AS " + query);
            database.DataStorage.BufferPoolCapacity.ShouldBe(128);
            TableAsPoolCount(TableAsPool(database.DataStorage)).ShouldBe(128);
            long baseline = 0, peak = 0; int read = 0, encoded = 0, copies = 0; long workspace = 0;
            var weak = new List<WeakReference>(); var retained = new List<object>();
            SqlPlanExecutor.TableAsSourceAdmitted = _ => baseline = LiveHeap.Read();
            SqlPlanExecutor.TableAsSourceReading = (source, row) =>
            {
                read++; workspace = Math.Max(workspace, Sorted(source)!.BufferBytes);
                var values = TableAsValues((SqlMaterializedRow)row);
                if (mutant) { retained.Add(values[1]!); }
                if (read % 97 == 0) { weak.Add(new(row)); weak.Add(new(values)); weak.Add(new(values[1]!)); }
            };
            SqlPlanExecutor.TableAsRecordEncoded = record =>
            {
                if (++encoded % 97 == 0) { weak.Add(new(record)); }
                if (encoded % 256 == 0) { peak = Math.Max(peak, LiveHeap.Read() - baseline); }
            };
            SqlPlanExecutor.TableAsStage = (phase, _) =>
            {
                if (phase == "Copied" && ++copies % 7 == 0) { peak = Math.Max(peak, LiveHeap.Read() - baseline); }
            };
            try
            {
                // Act
                (await session.ExecuteAsync("CREATE TABLE archive AS " + query)).AffectedCount.ShouldBe(count);
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                // Assert: the exact same guard rejects retaining one decoded string per row.
                void AssertBound() => peak.ShouldBeLessThanOrEqualTo(grant + constant);
                if (mutant) { Should.Throw<ShouldAssertException>(AssertBound); retained.Count.ShouldBe(count); }
                else { AssertBound(); weak.Count(w => w.IsAlive).ShouldBe(0); }
                read.ShouldBe(count); encoded.ShouldBe(count); copies.ShouldBe((count + 63) / 64);
                workspace.ShouldBeGreaterThan(0); // the merge fork was actually opened
                TableAsPoolCount(TableAsPool(database.DataStorage)).ShouldBe(128);
                peaks.Add(peak);
                _output.WriteLine($"CTAS_LIVE|mutant={mutant}|rows={count}|peak={peak}|workspace={workspace}|weak={weak.Count}|alive={weak.Count(w => w.IsAlive)}|poolPages=128");
                TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
            }
            finally { SqlPlanExecutor.TableAsSourceAdmitted = null; SqlPlanExecutor.TableAsSourceReading = null; SqlPlanExecutor.TableAsRecordEncoded = null; SqlPlanExecutor.TableAsStage = null; GC.KeepAlive(retained); }
        }
        if (!mutant) { Math.Abs(peaks[1] - peaks[0]).ShouldBeLessThanOrEqualTo(524288); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - CTAS brackets keep metadata during admission pause and copy each row once (#1283, #1789)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_TableAsCheckpointPause_ShouldResumeWithoutRepeatingRows(bool spill)
    {
        // Arrange: open a counted admission pause only after the first copied bracket.
        using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var engine = await Create("ctas-pause", spill ? 262144 : 67108864);
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
            await using (var other = await database.CreateSessionAsync(guard.Token))
            {
                var failure = await Should.ThrowAsync<DatabaseObjectLockedException>(async () =>
                    await other.ExecuteAsync("CREATE VIEW parked_view AS SELECT 1 AS id", cancellationToken: guard.Token));
                failure.Operation.ShouldBe("SQL definition DDL");
                _output.WriteLine($"CTAS_PARKED|spill={spill}|viewRefusal={failure.Operation}|diagnostics={string.Join(',', failure.Diagnostics.Select(d => d.Code))}");
                (await other.ExecuteAsync("CREATE TABLE unrelated(id INT)", cancellationToken: guard.Token)).Status.ShouldBe(DatabaseResultStatus.Success);
                database.Catalog.TryGetTable("dbo", "unrelated", out _).ShouldBeTrue();
                var tableFailure = await Should.ThrowAsync<DatabaseException>(async () =>
                    await other.ExecuteAsync("CREATE TABLE needs_data(id INT PRIMARY KEY)", cancellationToken: guard.Token));
                tableFailure.Diagnostics[0].Code.ShouldBe("CK04014");
                _output.WriteLine($"CTAS_PARKED|spill={spill}|unrelatedTable=Success|indexBuildRefusal={tableFailure.Diagnostics[0].Code}");
            }
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
                database.Catalog.GetPendingTableReclamations().ShouldNotContain(item => item == owner);
                await using var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT id,note FROM archive", cancellationToken: guard.Token);
                var rows = await Rows(result, guard.Token);
                rows.Select(row => row.Cells[0]).ShouldBe(Enumerable.Range(0, 180).Reverse().Cast<object?>());
                rows.ShouldAllBe(row => Equals(row.Cells[1], new string('x', 1100)));
            }
            else
            {
                absent++; await ReclaimTableAsAsync(database, guard.Token); database.DataStorage.HasOwnerPages(owner).ShouldBeFalse();
                await using (var source = (DatabaseResultSet)await session.ExecuteAsync("SELECT COUNT(*) FROM t", cancellationToken: guard.Token))
                { (await Rows(source, guard.Token)).ShouldHaveSingleItem().Cells[0].ShouldBe(180L); }
                (await session.ExecuteAsync("CREATE TABLE archive AS SELECT * FROM t ORDER BY id DESC", cancellationToken: guard.Token)).AffectedCount.ShouldBe(180);
            }
            database.IsOffline.ShouldBeFalse();
            Directory.GetFiles(temp, "*.ctmp").ShouldBeEmpty();
        }
        absent.ShouldBeGreaterThan(0); complete.ShouldBeGreaterThan(0);
        _output.WriteLine($"CTAS_CRASH|images={recorder.Count + 1}|absent={absent}|complete={complete}|brackets={copied}");
    }
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_values")]
    private static extern ref object?[] TableAsValues(SqlMaterializedRow row);

    private const string tableAsPoolType = "Assimalign.Cohesion.Database.Storage.Internal.StorageBufferPool, Assimalign.Cohesion.Database.Storage";
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_BufferPool")]
    [return: UnsafeAccessorType(tableAsPoolType)]
    private static extern object TableAsPool(global::Assimalign.Cohesion.Database.Storage.Storage storage);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Count")]
    private static extern int TableAsPoolCount([UnsafeAccessorType(tableAsPoolType)] object pool);
}
