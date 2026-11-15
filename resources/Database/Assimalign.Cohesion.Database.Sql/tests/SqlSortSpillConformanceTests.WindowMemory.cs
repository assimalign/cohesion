using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Window input membership caches remain charged throughout every spilled stage (#1789)")]
    public async Task ExecuteAsync_WindowCachedInputLiveHeap_ShouldCountPendingVectors()
    {
        // Arrange: the outer scalar owns the frame shared with the derived body's IN vector.
        const long grant = 4194304, floor = grant - 131072;
        using var pool = new HashPoolEvents();
        using var pooledImages = new WindowPoolLease();
        foreach (int count in new[] { 32768, 65536 })
        {
            await using var engine = await CreateWindowAsync("window-cache-live-" + count, grant, quota: 536870912);
            await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
            await Seed(session, count / 256);
            await session.ExecuteAsync("INSERT INTO u VALUES" + string.Join(',', Enumerable.Range(2, 255).Select(i => $"({i})")));
            await session.ExecuteAsync("CREATE TABLE big(k INT)");
            for (int first = 0; first < 8192; first += 128)
            { await session.ExecuteAsync("INSERT INTO big VALUES" + string.Join(',', Enumerable.Range(first, 128).Select(i => $"({i%73})"))); }
            const string sql = "SELECT d.id,ROW_NUMBER() OVER(PARTITION BY d.id%100 ORDER BY d.id) FROM (SELECT id FROM t WHERE k IN (SELECT k FROM big)) d CROSS JOIN u WHERE d.id>(SELECT MIN(id) FROM t)";
            if (Environment.GetEnvironmentVariable("SQ_L4B_RECORD") is { } reference)
            {
                string code = "ok";
                try { await WarmWindowHeapAsync(session, sql); }
                catch (DatabaseException failure) { code = failure.Diagnostics[0].Code!; }
                Directory.CreateDirectory(reference);
                await File.WriteAllTextAsync(Path.Combine(reference, $"cache-live-{count}.txt"), code);
                _output.WriteLine($"WINDOW_CACHE_REFERENCE|rows={count}|G={grant}|code={code}");
                continue;
            }
            await WarmWindowHeapAsync(session, sql);
            pooledImages.Drain(pool);
            if (count == 32768) { await QuiesceWindowPoolAsync(pool); }
            long before = LiveHeap.Read(), peak = before, pending = -1;
            int decoded = 0, resets = 0, drains = 0;
            SqlRowCollector.ObserveBuffer = (phase, value) =>
            {
                if (phase == "window-reset") { resets++; pending = ((SqlSubqueryValues)value).PendingStatementMemoryBytes; }
                if (phase == "window-drain" && drains++ == 0 || phase == "window-decoded" && ++decoded % 4096 == 0)
                { peak = Math.Max(peak, LiveHeap.Read()); }
            };
            try
            {
                // Act
                await using (var result = (DatabaseResultSet)await session.ExecuteAsync(sql)) { }
                long after = LiveHeap.Read(), measured = peak - Math.Min(before, after);
                _output.WriteLine($"WINDOW_CACHE_HEAP|rows={count}|peak={measured}|floor={floor}|before={before}|after={after}|pending={pending}|resets={resets}|drains={drains}");
                // Assert: the floor includes the cache plus the sort's remaining workspace.
                measured.ShouldBeGreaterThanOrEqualTo(floor);
                measured.ShouldBeLessThanOrEqualTo(grant + 131072 + 65536);
                resets.ShouldBe(1); drains.ShouldBeGreaterThan(0); pending.ShouldBe(0);
                session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0); session.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);
                TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
            }
            finally { SqlRowCollector.ObserveBuffer = null; }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Window live heap during execution stays bounded and rejects retained partition rows and per-row boxes (#1789)")]
    public async Task ExecuteAsync_WindowLiveHeap_ShouldRejectRetentionMutants()
    {
        // Arrange: every execution uses the lower quiescent reading before and after it.
        // This is a real heap bound, including all decoded objects, not a ledger peak.
        const long grant = 1048576, handoff = 131072, runtimeSlack = 65536, floor = grant - 131072;
        var peaks = new long[4, 2];
        var builder = SqlDatabaseEngine.CreateBuilder("window-live");
        builder.Options.RootPath = Path.Combine(_root, "window-live");
        builder.Options.StatementMemoryBudget = grant; builder.Options.StatementTempSpaceQuota = 536870912;
        builder.Options.BufferPoolCapacity = 1048576;
        await using var engine = await builder.BuildAsync();
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t(id INT,note TEXT)");
        for (int first = 0; first < 32768; first += 128)
        { await session.ExecuteAsync("INSERT INTO t VALUES" + string.Join(',', Enumerable.Range(first, 128).Select(i => $"({i},'{new string('x', 2048)}')"))); }
        using var pool = new HashPoolEvents();
        using var pooledImages = new WindowPoolLease();
        await WarmWindowHeapAsync(session, "SELECT id,note,ROW_NUMBER() OVER(PARTITION BY id%2048 ORDER BY id) FROM t WHERE id<16384");
        pooledImages.Drain(pool);
        await QuiesceWindowPoolAsync(pool);
        foreach (int count in new[] { 16384, 32768 })
        {
            string sql = $"SELECT id,note,ROW_NUMBER() OVER(PARTITION BY id%{count / 8} ORDER BY id) FROM t WHERE id<{count}";
            await WarmWindowHeapAsync(session, sql);
            for (int mutant = 0; mutant < 4; mutant++)
            {
                long minimum = long.MaxValue;
                for (int execution = 0; execution < 1; execution++)
                {
                    pooledImages.Drain(pool);
                    var retained = new List<object>();
                    WeakReference? sample = null;
                    long before = LiveHeap.Read(), peak = before, trimmed = pool.Trimmed;
                    int decoded = 0, partitions = 0, samples = 0, drains = 0, weakChecks = 0;
                    SqlRowCollector.ObserveBuffer = (phase, value) =>
                    {
                        if (phase == "window-decoded")
                        {
                            var row = (object?[])value;
                            if (mutant == 2) { retained.Add(row[0]!); }
                            if (++decoded % 4096 == 0) { Sample(); }
                        }
                        if (phase == "window-partition")
                        {
                            var row = ((List<object?[]>)value)[0];
                            if (sample is not null && partitions == 1)
                            {
                                LiveHeap.Read(); weakChecks++;
                                if (mutant == 1) { sample.IsAlive.ShouldBeTrue(); }
                                else { sample.IsAlive.ShouldBeFalse(); }
                            }
                            if (partitions == 0) { sample = new WeakReference(row); }
                            if (mutant == 1) { retained.Add(row); }
                            if (mutant == 3)
                            { foreach (var inputRow in (List<object?[]>)value) { retained.Add(inputRow[0]!); } }
                            if (++partitions % 128 == 0) { Sample(); }
                        }
                        if (phase == "window-drain" && drains++ == 0) { Sample(); }
                    };
                    try
                    {
                        // Act: mutants retain objects from the actual decode/partition path.
                        await using (var result = (DatabaseResultSet)await session.ExecuteAsync(sql)) { }
                        // Assert
                        decoded.ShouldBeGreaterThan(count); partitions.ShouldBe(count / 8); samples.ShouldBeGreaterThan(1);
                        weakChecks.ShouldBe(1); drains.ShouldBeGreaterThan(0);
                        if (mutant == 3) { retained.Count.ShouldBe(count); }
                        GC.KeepAlive(retained);
                        retained.Clear();
                        long after = LiveHeap.Read();
                        long measured = peak - Math.Min(before, after);
                        measured.ShouldBeGreaterThanOrEqualTo(floor);
                        minimum = Math.Min(minimum, measured);
                        _output.WriteLine($"WINDOW_HEAP_SAMPLE|rows={count}|mutant={mutant}|execution={execution}|peak={measured}|floor={floor}|before={before}|after={after}|pool_trimmed={pool.Trimmed-trimmed}|drains={drains}");
                    }
                    finally
                    {
                        SqlRowCollector.ObserveBuffer = null; retained.Clear();
                    }
                    void Sample() { samples++; peak = Math.Max(peak, LiveHeap.Read()); }
                }
                _output.WriteLine($"WINDOW_HEAP|rows={count}|partitions={count / 8}|mutant={mutant}|peak={minimum}|grant={grant}|handoff={handoff}|runtime_slack={runtimeSlack}");
                void Bound() => minimum.ShouldBeLessThanOrEqualTo(grant + handoff + runtimeSlack);
                peaks[mutant, count == 16384 ? 0 : 1] = minimum;
                if (mutant == 0) { Bound(); }
                else { Should.Throw<ShouldAssertException>(Bound); }
            }
            TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        }
        Growth(0);
        Should.Throw<ShouldAssertException>(() => Growth(1));
        Should.Throw<ShouldAssertException>(() => Growth(2));
        Should.Throw<ShouldAssertException>(() => Growth(3));
        void Growth(int mutant) => (peaks[mutant, 1] - peaks[mutant, 0]).ShouldBeLessThanOrEqualTo(131072);
    }

    private static async Task WarmWindowHeapAsync(SqlDatabaseSession session, string sql)
    { await using var result = (DatabaseResultSet)await session.ExecuteAsync(sql); }

    private static async Task QuiesceWindowPoolAsync(HashPoolEvents events)
    {
        // Shared buckets stay held by the lease. Other threads' free TLS images can
        // otherwise disappear during a later gen-2 collection. An untouched pooled
        // probe must be collected before sampling; its weak reference decides readiness.
        long trimmed = events.Trimmed;
        using var release = new ManualResetEventSlim();
        var ready = new TaskCompletionSource<WeakReference>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            // Keep this TLS owner alive until trimming, independently of pool-worker retirement.
            try { ready.SetResult(ReturnWindowPoolProbe()); release.Wait(); }
            catch (Exception failure) { ready.TrySetException(failure); }
        }) { IsBackground = true, Name = "window-pool-probe" };
        thread.Start();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            var probe = await ready.Task.WaitAsync(guard.Token);
            while (true)
            {
                LiveHeap.Read();
                if (!probe.IsAlive) { break; }
                await Task.Delay(100, guard.Token);
            }
            (events.Trimmed - trimmed).ShouldBeGreaterThanOrEqualTo(1048600);
        }
        finally { release.Set(); thread.Join(TimeSpan.FromSeconds(30)).ShouldBeTrue(); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ReturnWindowPoolProbe()
    {
        var buffer = ArrayPool<byte>.Shared.Rent(1048576);
        var weak = new WeakReference(buffer);
        ArrayPool<byte>.Shared.Return(buffer);
        return weak;
    }

    private static List<byte[]> PinWindowPool(HashPoolEvents events)
    {
        // Hold pre-existing pooled page images across both baselines and every sample.
        // A new allocation proves that each relevant shared bucket has been drained.
        var held = new List<byte[]>();
        foreach (int size in new[] { 8192, 16384, 32768, 65536, 131072, 262144, 524288, 1048576 })
        {
            long allocated = events.Allocated;
            int rents = 0;
            do { held.Add(ArrayPool<byte>.Shared.Rent(size)); rents++; }
            while (events.Allocated == allocated && rents < 1024);
            events.Allocated.ShouldBeGreaterThan(allocated);
        }
        return held;
    }

    private sealed class WindowPoolLease : IDisposable
    {
        private readonly List<byte[]> _buffers = [];
        internal void Drain(HashPoolEvents events) => _buffers.AddRange(PinWindowPool(events));
        public void Dispose()
        { foreach (var buffer in _buffers) { ArrayPool<byte>.Shared.Return(buffer); } _buffers.Clear(); }
    }
}
