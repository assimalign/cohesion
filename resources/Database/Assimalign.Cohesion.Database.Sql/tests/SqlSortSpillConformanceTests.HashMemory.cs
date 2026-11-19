using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Globalization;
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
using Assimalign.Cohesion.Database.Types;
using Assimalign.Cohesion.Testing;

using static Assimalign.Cohesion.Database.Sql.Tests.TestObjects.SqlHeapPool;
using static Assimalign.Cohesion.Database.Sql.Tests.TestObjects.SqlHeapAcceptance;

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
        using var pool = new HashPoolEvents();
        await using var batch = new Batch(pool, _output, nameof(ExecuteAsync_HashLiveObjects_ShouldRejectRetainingMutant));
        var partitionHash = SqlPlanExecutor.HashSpillPartitionHash;
        SqlPlanExecutor.HashSpillPartitionHash = hash => hash & ~7;
        try
        {
            // Act: separate fully warmed engines exclude table storage from each sample.
            var small = await MeasureHashLiveAsync(12000, batch, pool);
            var large = await MeasureHashLiveAsync(24000, batch, pool);

            // Assert: doubling row count must not retain another row-sized replay population.
            long Replay(Sample<HashHeapReading>[] population) => population.Min(x => x.Value.Replay);
            await batch.CheckAsync(() => [Ceiling("hash replay population growth", Replay(large.Normal) - Replay(small.Normal), 131072,
                Term.Maximum(large.Normal) + Term.Maximum(small.Normal)),
                ..Enumerable.Range(0, 3).Select(execution => Floor("small hash mutant growth " + execution,
                    large.SmallMutant[execution].Value.Replay - small.SmallMutant[execution].Value.Replay, 131072,
                    large.SmallMutant[execution].Term + small.SmallMutant[execution].Term, true))], () =>
            {
                long smallReplay = Replay(small.Normal), largeReplay = Replay(large.Normal);
                (largeReplay - smallReplay).ShouldBeLessThanOrEqualTo(131072);
                _output.WriteLine($"HASH_LIVE_DELTA|small={smallReplay}|large={largeReplay}|delta={largeReplay - smallReplay}|maximum=131072");
                for (int execution = 0; execution < small.SmallMutant.Length; execution++)
                {
                    long delta = large.SmallMutant[execution].Value.Replay - small.SmallMutant[execution].Value.Replay;
                    _output.WriteLine($"HASH_SMALL_MUTANT_DELTA|execution={execution + 1}|small={small.SmallMutant[execution].Value.Replay}|large={large.SmallMutant[execution].Value.Replay}|delta={delta}|maximum=131072");
                    delta.ShouldBeGreaterThan(131072);
                    Should.Throw<ShouldAssertException>(() => delta.ShouldBeLessThanOrEqualTo(131072));
                }
            });
            await batch.CompleteAsync();
        }
        finally { SqlPlanExecutor.HashSpillPartitionHash = partitionHash; }
    }

    private async Task<(Sample<HashHeapReading>[] Normal, Sample<HashHeapReading>[] SmallMutant)> MeasureHashLiveAsync(int count, Batch batch, HashPoolEvents pool)
    {
        var measurements = new List<Sample<HashHeapReading>>();
        var normal = new Sample<HashHeapReading>[3];
        var smallMutant = new Sample<HashHeapReading>[3];
        // Arrange
        const long grant = 1048576, allowance = 262144;
        bool observe = false;
        int mutant = 0;
        var retained = new List<object>(count); var weak = new List<WeakReference>();
        var builder = SqlDatabaseEngine.CreateBuilder("hash-live");
        builder.Options.TempDirectory = Path.Combine(_root, ".hash-live");
        builder.Options.StatementMemoryBudget = grant; builder.Options.StatementTempSpaceQuota = 268435456;
        builder.Functions.Add(SqlScalarFunction.Create("fresh_key", (long id) =>
        {
            string value = new string('x', 128) + id.ToString(CultureInfo.InvariantCulture);
            if (observe && id is >= 5000 and < 5200) { weak.Add(new WeakReference(value)); }
            if (mutant != 0) { retained.Add(new byte[mutant == 1 ? 1024 : 40]); }
            return value;
        }));
        var engine = await builder.BuildAsync();
        await using var engineLease = batch.LeaseAsync(engine, measurements);
        var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await using var sessionLease = batch.LeaseAsync(session, measurements);
        await Seed(session, count);
        const string sql = "SELECT fresh_key(id),COUNT(*) FROM t GROUP BY fresh_key(id)";
        await using (var warm = (DatabaseResultSet)await session.ExecuteAsync(sql)) { }
        // Keep pre-existing pool images from disappearing during either population.
        var images = new WindowPoolLease();
        await using var imagesLease = batch.Lease(images, measurements);
        images.Drain(pool);

        try
        {
            for (int modeIndex = 0; modeIndex <= 2; modeIndex++)
            {
                int mode = modeIndex;
                for (int executionIndex = 1; executionIndex <= (mode == 1 ? 1 : 3); executionIndex++)
                {
                    int execution = executionIndex, weakCapacity = weak.Capacity, retainedCapacity = retained.Capacity;
                    var sampled = await batch.MeasureAsync($"rows={count}|execution={execution}|mutant={mode}", async readHeap =>
                    {
                        weak.Clear(); weak.Capacity = weakCapacity;
                        retained.Clear(); retained.Capacity = retainedCapacity; mutant = mode;
                        images.Drain(pool);
                        long before = readHeap(), peak = 0, replayPeak = 0, replayDepth = 0;
                        long trimmedBefore = pool.Trimmed, rentedBefore = pool.Rented, allocatedBefore = pool.Allocated, droppedBefore = pool.Dropped, unknownBefore = pool.Unknown;
                        bool replaySampled = false;
                        var phasePeaks = new Dictionary<string, long>();
                        observe = true;
                        SqlPlanExecutor.ObserveHashSpill = (phase, ordinal) =>
                        {
                            if (phase is "batch" or "batch-live") { replaySampled = true; }
                            if (phase == "batch") { replayDepth = Math.Max(replayDepth, ordinal); }
                            if (phase == "input" && ordinal % 2000 == 0 || phase is "ordered" or "batch" or "batch-live")
                            {
                                long live = readHeap();
                                peak = Math.Max(peak, live);
                                if (phase is "batch" or "batch-live" or "ordered") { replayPeak = Math.Max(replayPeak, live); }
                                phasePeaks[phase] = Math.Max(phasePeaks.GetValueOrDefault(phase), live);
                            }
                            if (phase == "ordered") { weak.Count.ShouldBe(200); weak.Count(w => w.IsAlive).ShouldBe(0); }
                        };
                        // Act: every execution has its own quiescent readings in this warmed engine.
                        await using (var result = (DatabaseResultSet)await session.ExecuteAsync(sql))
                        { int visited = 0; await foreach (var row in result.GetRowsAsync()) { row.GetString(0).ShouldNotBeNull(); visited++; } visited.ShouldBe(count); }

                        // Assert
                        long after = readHeap();
                        // Absolute bound: the pre-run baseline; ambient reclamation cannot fail it falsely.
                        peak = Math.Max(0, peak - before);
                        // Compare with memory present before this execution. Subtracting a lower
                        // after-reading would charge reclaimed pre-existing pool buffers as replay.
                        // Three executions minimize transient roots; the 64 B/row mutant below
                        // exercises the same sampling and comparison under that reclamation.
                        long baseline = before;
                        replayPeak = Math.Max(0, replayPeak - baseline);
                        _output.WriteLine($"HASH_LIVE|rows={count}|execution={execution}|mutant={mode}|bytes={peak}|grant={grant}|constant={allowance}|replay={replayPeak}|depth={replayDepth}|baseline_before={before}|baseline_after={after}|trimmed_bytes={pool.Trimmed - trimmedBefore}|rented_bytes={pool.Rented - rentedBefore}|allocated_bytes={pool.Allocated - allocatedBefore}|dropped_bytes={pool.Dropped - droppedBefore}|unknown_events={pool.Unknown - unknownBefore}|phases={string.Join(',', phasePeaks.Select(p => p.Key + ":" + Math.Max(0, p.Value - baseline)))}");
                        SqlPlanExecutor.ObserveHashSpill = null; observe = false;
                        replaySampled.ShouldBeTrue(); replayDepth.ShouldBeGreaterThan(1);
                        if (mode != 0) { retained.Count.ShouldBe(count); }
                        GC.KeepAlive(retained);
                        return new HashHeapReading(peak, replayPeak, mode == 1 ? phasePeaks["ordered"] - before : 0, retained.Count);
                    });
                    measurements.Add(sampled);
                    if (mode == 0) { normal[execution - 1] = sampled; }
                    if (mode == 2) { smallMutant[execution - 1] = sampled; }
                    await batch.CheckAsync(() => [Floor("hash replay > 0", sampled.Value.Replay, 0, sampled.Term, true),
                        Floor("hash peak > 0", sampled.Value.Peak, 0, sampled.Term, true),
                        ..(mode == 0 ? new[] { Ceiling("hash execution peak", sampled.Value.Peak, grant + allowance, sampled.Term) } : []),
                        ..(mode == 1 ? new[] { Floor("large hash mutant ordered peak", sampled.Value.Ordered, grant + allowance, sampled.Term, true) } : [])], () =>
                    {
                        long replayPeak = sampled.Value.Replay, peak = sampled.Value.Peak;
                        replayPeak.ShouldBeGreaterThan(0);
                        peak.ShouldBeGreaterThan(0);
                        if (mode == 0)
                        {
                            peak.ShouldBeLessThanOrEqualTo(grant + allowance);
                        }
                        else
                        {
                            if (mode == 1)
                            {
                                sampled.Value.Ordered.ShouldBeGreaterThan(grant + allowance);
                            }

                            _output.WriteLine($"HASH_MUTANT|rows={count}|execution={execution}|object_bytes={(mode == 1 ? 1048 : 64)}|bytes={peak}|objects={sampled.Value.Retained}");
                        }
                    });
                }
            }
        }
        finally { SqlPlanExecutor.ObserveHashSpill = null; retained.Clear(); }
        TempBytes(engine.TempSpace!).ShouldBe(0); session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
        return (normal, smallMutant);
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
    private sealed record HashHeapReading(long Peak, long Replay, long Ordered, int Retained);

}
