using System;
using System.Collections.Generic;
using System.Globalization;
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
    [Fact(DisplayName = "Cohesion Test [Sql] - Hash and downstream sort share one quota owner and one memory grant (#1789)")]
    public async Task ExecuteAsync_HashThenSort_ShouldShareOneQuotaOwner()
    {
        // Arrange
        const long grant = 262144;
        var builder = SqlDatabaseEngine.CreateBuilder("hash-shared-owner");
        builder.Options.StatementMemoryBudget = grant; builder.Options.TempDirectory = Path.Combine(_root, ".shared-owner");
        await using var engine = await builder.BuildAsync();
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Seed(session, 8000);
        bool observed = false;
        SqlRowCollector.ObserveSpill = phase =>
        {
            if (phase != "handoff") { return; }
            observed = true;
            Statements(engine.TempSpace!).ShouldHaveSingleItem().Quota.ShouldBe(4 * grant);
        };
        try
        {
            // Act
            await using var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT id,COUNT(*) FROM t GROUP BY id ORDER BY id DESC");
            int expected = 7999;
            await foreach (var row in result.GetRowsAsync()) { row.GetInt32(0).ShouldBe(expected--); }

            // Assert
            expected.ShouldBe(-1); observed.ShouldBeTrue();
            session.LastStatementMetrics!.PeakHashMemoryBytes.ShouldBeLessThanOrEqualTo(grant);
            session.LastStatementMetrics.HashMemoryBytes.ShouldBe(0); session.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);
            Statements(engine.TempSpace!).ShouldHaveSingleItem().PeakBytes.ShouldBeLessThanOrEqualTo(4 * grant);
        }
        finally { SqlRowCollector.ObserveSpill = null; }
        TempBytes(engine.TempSpace!).ShouldBe(0); Statements(engine.TempSpace!).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Hash spill capacity: ten grants complete under default quotas with and without file storage (#1789)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_TenHashGrants_ShouldUseDefaultQuota(bool memory)
    {
        // Arrange: a key costs 96 B plus 128 B per group and 128 B per COUNT.
        const int grant = 262144, count = 8000;
        (count * 352L).ShouldBeGreaterThan(10L * grant);
        var builder = SqlDatabaseEngine.CreateBuilder("hash-capacity");
        builder.Options.StatementMemoryBudget = grant;
        if (memory) { builder.Options.TempDirectory = Path.Combine(_root, ".hash-temp"); }
        else { builder.Options.RootPath = Path.Combine(_root, "hash-capacity"); }
        await using var engine = await builder.BuildAsync();
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Seed(session, count);

        // Act
        await using (var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT id,COUNT(*) FROM t GROUP BY id"))
        {
            int ordinal = 0;
            await foreach (var row in result.GetRowsAsync()) { row.GetInt32(0).ShouldBe(ordinal++); row.GetInt64(1).ShouldBe(1); }
            ordinal.ShouldBe(count);
            var metrics = session.LastStatementMetrics!;
            metrics.PeakHashMemoryBytes.ShouldBeLessThanOrEqualTo(grant);
            metrics.HashMemoryBytes.ShouldBe(0); metrics.PlainSelectMemoryBytes.ShouldBe(0);
            var spill = SqlHashSpillMetrics.For(metrics).ShouldNotBeNull();
            spill.PeakBytes.ShouldBeLessThanOrEqualTo(4L * grant);
            _output.WriteLine($"HASH_CAPACITY|memory={memory}|rows={count}|logical={count * 352L}|peak={spill.PeakBytes}|batches={spill.Batches}|passes={spill.Passes}");
        }

        // Assert
        TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        await using var analyzed = (DatabaseResultSet)await session.ExecuteAsync("EXPLAIN ANALYZE SELECT id,COUNT(*) FROM t GROUP BY id");
        var details = string.Join(' ', (await Rows(analyzed)).SelectMany(r => r.Cells).OfType<string>());
        details.ShouldContain("hash_spill_batches=", Case.Sensitive); details.ShouldContain("hash_spill_passes=", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Hash spill failure: temp refusals and read errors are located and keep the explicit transaction online (#1789)")]
    [InlineData("CK05001")]
    [InlineData("CK05002")]
    [InlineData("CK05003")]
    [InlineData("CK05004")]
    [InlineData("CK05005")]
    public async Task ExecuteAsync_HashTempFailure_ShouldKeepSessionUsable(string code)
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("hash-failure");
        builder.Options.StatementMemoryBudget = 262144;
        builder.Options.TempDirectory = Path.Combine(_root, ".hash-failure");
        if (code == "CK05001") { builder.Options.TempSpaceQuota = 0; }
        if (code == "CK05002") { builder.Options.StatementTempSpaceQuota = 0; }
        await using var engine = await builder.BuildAsync();
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Seed(session, 8000); await session.ExecuteAsync("BEGIN");
        bool injected = false;
        if (code == "CK05003") { Available(engine.TempSpace!) = () => 0; ProbeTick(engine.TempSpace!) = 0; injected = true; }
        if (code is "CK05004" or "CK05005")
        { BeforeRead(engine.TempSpace!, () => { injected = true; throw TempException(code, "injected hash read failure"); }); }
        try
        {
            // Act
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT id,COUNT(*) FROM t GROUP BY id"));

            // Assert
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
            error.Diagnostics[0].Start.ShouldNotBeNull(); error.Diagnostics[0].End.ShouldNotBeNull();
            if (code is "CK05003" or "CK05004" or "CK05005") { injected.ShouldBeTrue(); }
            TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
            session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0); session.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);
            (await session.ExecuteAsync("SELECT 1")).Status.ShouldBe(DatabaseResultStatus.Success);
            await session.ExecuteAsync("COMMIT"); engine.OfflineDatabases.ShouldBeEmpty();
        }
        finally { BeforeRead(engine.TempSpace!, null); }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Hash live heap: replay stays within 256 KiB scratch and growth below 128 KiB (#1789)")]
    public async Task ExecuteAsync_HashLiveObjects_ShouldRejectRetainingMutant()
    {
        // Arrange: both populations fill a replay batch before repartitioning. Keep later hash bits.
        var partitionHash = SqlPlanExecutor.HashSpillPartitionHash;
        SqlPlanExecutor.HashSpillPartitionHash = hash => hash & ~7;
        try
        {
            // Act: separate fully warmed engines exclude table storage from each sample.
            long small = await MeasureHashLiveAsync(12000);
            long large = await MeasureHashLiveAsync(24000);

            // Assert: doubling row count must not retain another row-sized replay population.
            (large - small).ShouldBeLessThanOrEqualTo(131072);
            _output.WriteLine($"HASH_LIVE_DELTA|small={small}|large={large}|delta={large-small}|maximum=131072");
        }
        finally { SqlPlanExecutor.HashSpillPartitionHash = partitionHash; }
    }

    private async Task<long> MeasureHashLiveAsync(int count)
    {
        // Arrange
        const long grant = 1048576, allowance = 262144;
        bool observe = false, mutant = false;
        var retained = new List<object>(); var weak = new List<WeakReference>();
        var builder = SqlDatabaseEngine.CreateBuilder("hash-live");
        builder.Options.TempDirectory = Path.Combine(_root, ".hash-live");
        builder.Options.StatementMemoryBudget = grant; builder.Options.StatementTempSpaceQuota = 268435456;
        builder.Functions.Add(SqlScalarFunction.Create("fresh_key", (long id) =>
        {
            string value = new string('x', 128) + id.ToString(CultureInfo.InvariantCulture);
            if (observe && id is >= 5000 and < 5200) { weak.Add(new WeakReference(value)); }
            if (mutant) { retained.Add(new byte[1024]); }
            return value;
        }));
        await using var engine = await builder.BuildAsync();
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Seed(session, count);
        const string sql = "SELECT fresh_key(id),COUNT(*) FROM t GROUP BY fresh_key(id)";
        await using (var warm = (DatabaseResultSet)await session.ExecuteAsync(sql)) { }
        long baseline = LiveHeap.Read(), peak = 0;
        long replayPeak = 0, replayDepth = 0; bool replaySampled = false;
        var phasePeaks = new Dictionary<string, long>();
        observe = true;
        SqlPlanExecutor.ObserveHashSpill = (phase, ordinal) =>
        {
            if (phase is "batch" or "batch-live") { replaySampled = true; }
            if (phase == "batch") { replayDepth = Math.Max(replayDepth, ordinal); }
            if (phase == "input" && ordinal % 2000 == 0 || phase is "ordered" or "batch" or "batch-live")
            {
                long live = LiveHeap.Read();
                peak = Math.Max(peak, live);
                if (phase is "batch" or "batch-live" or "ordered") { replayPeak = Math.Max(replayPeak, live); }
                phasePeaks[phase] = Math.Max(phasePeaks.GetValueOrDefault(phase), live);
            }
            if (phase == "ordered") { weak.Count.ShouldBe(200); weak.Count(w => w.IsAlive).ShouldBe(0); }
        };
        try
        {
            // Act
            await using (var result = (DatabaseResultSet)await session.ExecuteAsync(sql))
            { int visited = 0; await foreach (var row in result.GetRowsAsync()) { row.GetString(0).ShouldNotBeNull(); visited++; } visited.ShouldBe(count); }

            // Assert
            long after = LiveHeap.Read();
            long before = baseline;
            // Absolute bound: the pre-run baseline; ambient reclamation cannot fail it falsely.
            peak = Math.Max(0, peak - before);
            // Population delta: the lower quiescent baseline, so reclamation cannot hide replay growth.
            baseline = Math.Min(before, after);
            replayPeak = Math.Max(0, replayPeak - baseline);
            replaySampled.ShouldBeTrue(); replayDepth.ShouldBeGreaterThan(1);
            replayPeak.ShouldBeGreaterThan(0);
            peak.ShouldBeGreaterThan(0); peak.ShouldBeLessThanOrEqualTo(grant + allowance);
            _output.WriteLine($"HASH_LIVE|rows={count}|bytes={peak}|grant={grant}|constant={allowance}|replay={replayPeak}|depth={replayDepth}|baseline_before={before}|baseline_after={after}|phases={string.Join(',', phasePeaks.Select(p => p.Key + ":" + Math.Max(0, p.Value - baseline)))}");
            SqlPlanExecutor.ObserveHashSpill = null; observe = false; weak.Clear();
            baseline = LiveHeap.Read(); peak = 0; mutant = true;
            SqlPlanExecutor.ObserveHashSpill = (phase, _) => { if (phase == "ordered") { peak = LiveHeap.Read() - baseline; } };
            await using (var result = (DatabaseResultSet)await session.ExecuteAsync(sql)) { }
            retained.Count.ShouldBe(count); peak.ShouldBeGreaterThan(grant + allowance);
            _output.WriteLine($"HASH_MUTANT|rows={count}|bytes={peak}|objects={retained.Count}");
        }
        finally { SqlPlanExecutor.ObserveHashSpill = null; retained.Clear(); }
        TempBytes(engine.TempSpace!).ShouldBe(0); session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
        return replayPeak;
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Hash crash images: read-only spill boundaries preserve the exact durable state (#1789)")]
    public async Task Reopen_HashSpillBoundaries_ShouldKeepExactDurableRows()
    {
        // Arrange: the change adds no durable write or flush boundary. Capture images at
        // partition append and ordinal-merge completion, alongside every recorded durable call.
        var storage = new CrashCaptureSqlStorageStrategy();
        var snapshots = new List<CrashCaptureSqlStorageStrategy>();
        RowValues[] expected;
        var builder = SqlDatabaseEngine.CreateBuilder("hash-crash");
        builder.Options.StorageStrategy = storage; builder.Options.StatementMemoryBudget = 262144;
        builder.Options.TempDirectory = Path.Combine(_root, ".hash-crash");
        await using (var engine = await builder.BuildAsync())
        {
            var database = await engine.CreateDatabaseAsync("db");
            await using var session = await database.CreateSessionAsync(); await Seed(session, 8000);
            await using (var before = (DatabaseResultSet)await session.ExecuteAsync("SELECT id,k,note FROM t ORDER BY id"))
            { expected = await Rows(before); }
            database.Coordinator.Checkpoint(CancellationToken.None); database.CatalogStorage.Checkpoint();
            SqlPlanExecutor.ObserveHashSpill = (phase, _) =>
            { if (phase == "ordered" || phase == "input" && snapshots.Count == 0) { snapshots.Add(storage.CaptureDurableImages()); } };
            try
            {
                using var recorder = storage.RecordCrashPoints();
                await using var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT id,COUNT(*) FROM t GROUP BY id");
                recorder.Count.ShouldBe(1); snapshots.Count.ShouldBe(2);
            }
            finally { SqlPlanExecutor.ObserveHashSpill = null; }
        }

        // Act / Assert
        foreach (var snapshot in snapshots)
        {
            await using var reopened = SqlDatabaseEngine.Create("hash-reopen", new() { StorageStrategy = snapshot });
            await using var session = await (await reopened.OpenDatabaseAsync("db")).CreateSessionAsync();
            await using var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT id,k,note FROM t ORDER BY id");
            var rows = await Rows(result); rows.Length.ShouldBe(8000);
            rows.ShouldBe(expected);
            rows.Select(r => r.Cells[0]).ShouldBe(Enumerable.Range(0, 8000).Cast<object>());
            await using var schema = (DatabaseResultSet)await session.ExecuteAsync("SELECT COUNT(*) FROM u");
            (await Rows(schema)).ShouldHaveSingleItem().Cells[0].ShouldBe(1L);
        }
    }
}
