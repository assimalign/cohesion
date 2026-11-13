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
            var small = await MeasureHashLiveAsync(12000);
            var large = await MeasureHashLiveAsync(24000);

            // Assert: doubling row count must not retain another row-sized replay population.
            (large.Replay - small.Replay).ShouldBeLessThanOrEqualTo(131072);
            _output.WriteLine($"HASH_LIVE_DELTA|small={small.Replay}|large={large.Replay}|delta={large.Replay-small.Replay}|maximum=131072");
            for (int execution = 0; execution < small.SmallMutant.Length; execution++)
            {
                long delta = large.SmallMutant[execution] - small.SmallMutant[execution];
                _output.WriteLine($"HASH_SMALL_MUTANT_DELTA|execution={execution + 1}|small={small.SmallMutant[execution]}|large={large.SmallMutant[execution]}|delta={delta}|maximum=131072");
                delta.ShouldBeGreaterThan(131072);
                Should.Throw<ShouldAssertException>(() => delta.ShouldBeLessThanOrEqualTo(131072));
            }
        }
        finally { SqlPlanExecutor.HashSpillPartitionHash = partitionHash; }
    }

    private async Task<(long Replay, long[] SmallMutant)> MeasureHashLiveAsync(int count)
    {
        using var pool = new HashPoolEvents();
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
        await using var engine = await builder.BuildAsync();
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Seed(session, count);
        const string sql = "SELECT fresh_key(id),COUNT(*) FROM t GROUP BY fresh_key(id)";
        await using (var warm = (DatabaseResultSet)await session.ExecuteAsync(sql)) { }
        long minimumReplay = long.MaxValue;
        var smallMutant = new long[3];
        try
        {
            for (int mode = 0; mode <= 2; mode++)
            {
                for (int execution = 1; execution <= (mode == 1 ? 1 : 3); execution++)
                {
                    weak.Clear(); retained.Clear(); mutant = mode;
                    long before = LiveHeap.Read(), peak = 0, replayPeak = 0, replayDepth = 0;
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
                            long live = LiveHeap.Read();
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
                    long after = LiveHeap.Read();
                    // Absolute bound: the pre-run baseline; ambient reclamation cannot fail it falsely.
                    peak = Math.Max(0, peak - before);
                    // Compare with memory present before this execution. Subtracting a lower
                    // after-reading would charge reclaimed pre-existing pool buffers as replay.
                    // Three executions minimize transient roots; the 64 B/row mutant below
                    // exercises the same sampling and comparison under that reclamation.
                    long baseline = before;
                    replayPeak = Math.Max(0, replayPeak - baseline);
                    _output.WriteLine($"HASH_LIVE|rows={count}|execution={execution}|mutant={mode}|bytes={peak}|grant={grant}|constant={allowance}|replay={replayPeak}|depth={replayDepth}|baseline_before={before}|baseline_after={after}|trimmed_bytes={pool.Trimmed - trimmedBefore}|rented_bytes={pool.Rented - rentedBefore}|allocated_bytes={pool.Allocated - allocatedBefore}|dropped_bytes={pool.Dropped - droppedBefore}|unknown_events={pool.Unknown - unknownBefore}|phases={string.Join(',', phasePeaks.Select(p => p.Key + ":" + Math.Max(0, p.Value - baseline)))}");
                    replaySampled.ShouldBeTrue(); replayDepth.ShouldBeGreaterThan(1);
                    replayPeak.ShouldBeGreaterThan(0);
                    peak.ShouldBeGreaterThan(0);
                    if (mode == 0)
                    {
                        peak.ShouldBeLessThanOrEqualTo(grant + allowance);
                        minimumReplay = Math.Min(minimumReplay, replayPeak);
                    }
                    else
                    {
                        retained.Count.ShouldBe(count);
                        if (mode == 1) { (phasePeaks["ordered"] - before).ShouldBeGreaterThan(grant + allowance); }
                        else { smallMutant[execution - 1] = replayPeak; }
                        _output.WriteLine($"HASH_MUTANT|rows={count}|execution={execution}|object_bytes={(mode == 1 ? 1048 : 64)}|bytes={peak}|objects={retained.Count}");
                    }
                    SqlPlanExecutor.ObserveHashSpill = null; observe = false;
                }
            }
        }
        finally { SqlPlanExecutor.ObserveHashSpill = null; retained.Clear(); }
        TempBytes(engine.TempSpace!).ShouldBe(0); session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
        return (minimumReplay, smallMutant);
    }

    private sealed class HashPoolEvents : EventListener
    {
        private static readonly int _bytes = ArrayPool<byte>.Shared.GetHashCode(), _chars = ArrayPool<char>.Shared.GetHashCode(),
            _objects = ArrayPool<object>.Shared.GetHashCode(), _ints = ArrayPool<int>.Shared.GetHashCode(), _values = ArrayPool<SqlValue>.Shared.GetHashCode();
        internal long Trimmed, Rented, Allocated, Dropped, Unknown;
        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "System.Buffers.ArrayPoolEventSource") { EnableEvents(source, EventLevel.Verbose); }
        }
        protected override void OnEventWritten(EventWrittenEventArgs args)
        {
            if (args.EventId is not (1 or 2 or 4 or 6) || args.Payload is null || args.Payload.Count < 3) { return; }
            int length = (int)args.Payload[1]!, pool = (int)args.Payload[2]!;
            int width = pool == _bytes ? 1 : pool == _chars ? 2 : pool == _objects ? IntPtr.Size : pool == _ints ? 4 : pool == _values ? Unsafe.SizeOf<SqlValue>() : 0;
            if (width == 0) { Interlocked.Increment(ref Unknown); return; }
            long size = (24L + (long)length * width + 7) & ~7L;
            if (args.EventId == 1) { Interlocked.Add(ref Rented, size); }
            if (args.EventId == 2) { Interlocked.Add(ref Allocated, size); }
            if (args.EventId == 4) { Interlocked.Add(ref Trimmed, size); }
            if (args.EventId == 6) { Interlocked.Add(ref Dropped, size); }
        }
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
