using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Testing;

using static Assimalign.Cohesion.Database.Sql.Tests.TestObjects.SqlHeapPool;
using static Assimalign.Cohesion.Database.Sql.Tests.TestObjects.SqlHeapAcceptance;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Spill reader and writer workspaces stay on the live ledger (#1789)")]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task ExecuteAsync_SetInnerSpillCharges_ShouldHoldWorkspace(int shape)
    {
        // Arrange: the first stage append precedes its optional key reservations.
        // TryPartition starts with only its two-run writer and sealed metadata.
        await using var engine = await Create("set-inner-charges-" + shape, 262144, quota: 134217728);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync(); await Seed(session, 8192);
        int samples = 0, selected = 0;
        SqlRowCollector.ObserveBuffer = (phase, value) =>
        {
            long charged = session.LastStatementMetrics!.PlainSelectMemoryBytes;
            if (shape == 0 && phase == "inner-decoded")
            {
                var row = (object?[])value;
                SqlTempRowCodec.TryGetLength(row, out int length).ShouldBeTrue();
                charged.ShouldBeGreaterThanOrEqualTo(8192 + length + 256 + 2 * SqlTempRowCodec.DecodedBytes(row));
                samples++;
            }
            else if (shape != 0 && phase == "set-append" && samples++ == 0)
            { charged.ShouldBeGreaterThanOrEqualTo(shape == 1 ? 79616 : 25344); }
            else if (shape == 3 && phase == "set-selected")
            { charged.ShouldBeGreaterThanOrEqualTo(25344 + 512 + 8192 + 8704); selected++; }
        };
        try
        {
            // Act
            await using (var result = (DatabaseResultSet)await session.ExecuteAsync(shape switch
            {
                0 => "SELECT u.id FROM u JOIN t ON t.id<u.id*0",
                1 => "SELECT id FROM t UNION SELECT id FROM t",
                2 => "SELECT id FROM t UNION ALL SELECT id FROM t",
                _ => "SELECT id FROM t INTERSECT SELECT id FROM t",
            })) { }
            // Assert
            samples.ShouldBeGreaterThan(0); if (shape == 3) { selected.ShouldBeGreaterThan(1); }
            session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
            TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        }
        finally { SqlRowCollector.ObserveBuffer = null; }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - During-operation set/inner heap bounds reject per-input boxes and one kept decoded row per pass (#1789)")]
    [InlineData(0, 0)] [InlineData(0, 1)] [InlineData(1, 0)] [InlineData(1, 1)]
    [InlineData(2, 0)] [InlineData(2, 1)] [InlineData(3, 0)] [InlineData(3, 1)]
    public async Task ExecuteAsync_SetInnerLiveHeap_ShouldRejectRetentionMutant(int shape, int mutant)
    {
        // Arrange: a separate baseline for every execution, with reusable pool images
        // rooted before sampling. The allowance beyond G is fixed at 64 KiB. Each mutant
        // is its own case: its verdict reads only its own two populations, and both
        // mutants in one case outlasted CI's five-minute per-test hang limit on macOS.
        bool inner = shape == 1;
        long grant = inner ? 32768 : 262144;
        // UNION replay's admitted dictionary plus reader reaches at least G -
        // 160 KiB. INTERSECT keeps encoding scratch, two writer pages and a reader;
        // UNION ALL keeps its encoding scratch and writer page. These are live floors.
        long floor = shape switch { 0 => grant - 163840, 1 => 8192, 2 => 40960, _ => 24576 };
        int[] populations = inner ? [256, 4096] : [8192, 32768];
        await using var engine = await Create("set-inner-live-" + shape, grant, quota: 134217728);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE live(id INT,note TEXT)");
        if (inner)
        {
            await session.ExecuteAsync("INSERT INTO live VALUES" + string.Join(',', Enumerable.Range(0, 128).Select(i => $"({i},'{new string('x', 600)}')")));
            await session.ExecuteAsync("CREATE TABLE outer_rows(id INT)");
            for (int first = 0; first < populations[1]; first += 128)
            { await session.ExecuteAsync("INSERT INTO outer_rows VALUES" + string.Join(',', Enumerable.Range(first, 128).Select(i => $"({i})"))); }
        }
        else
        {
            for (int first = 0; first < populations[1]; first += 128)
            { await session.ExecuteAsync("INSERT INTO live VALUES" + string.Join(',', Enumerable.Range(first, 128).Select(i => $"({i},'a')"))); }
        }
        using var pool = new HashPoolEvents();
        using var images = new WindowPoolLease();
        await WarmAsync(populations[1]); images.Drain(pool);
        var peaks = new long[2];
        var batch = new Batch(pool, _output, nameof(ExecuteAsync_SetInnerLiveHeap_ShouldRejectRetentionMutant));
        List<Sample<SetHeapReading>>[] rounds = [[], []];
        for (int populationIndex = 0; populationIndex < 2; populationIndex++)
        {
            int population = populationIndex;
            await WarmAsync(populations[population]);
            long minimum = long.MaxValue;
            for (int executionIndex = 0; executionIndex < 3; executionIndex++)
            {
                int execution = executionIndex;
                var sampled = await batch.MeasureAsync($"shape={shape}|population={populations[population]}|mutant={mutant}|execution={execution}", async readHeap =>
                {
                    images.Drain(pool);
                    var retained = new List<object>();
                    long peak = 0, before = 0;
                    long replayPeak = 0, orderedPeak = 0, appendPeak = 0, partitionPeak = 0;
                    int visits = 0, samples = 0, replaySamples = 0, orderedSamples = 0, appendSamples = 0, partitionSamples = 0;
                    bool partition = false;
                    SqlRowCollector.ObserveBuffer = (phase, value) =>
                    {
                        if (inner)
                        {
                            if (phase == "inner-append" && appendSamples == 0)
                            { appendSamples++; appendPeak = readHeap(); peak = Math.Max(peak, appendPeak); }
                            if (phase != "inner-decoded" || ((object?[])value)[0] is not int id || id != 0) { return; }
                            var row = (object?[])value;
                            SqlTempRowCodec.TryGetLength(row, out int length).ShouldBeTrue();
                            session.LastStatementMetrics!.PlainSelectMemoryBytes.ShouldBeGreaterThanOrEqualTo(
                                8192 + length + 256 + 2 * SqlTempRowCodec.DecodedBytes(row));
                            if (mutant != 0) { retained.Add(value); } // One decoded row per actual pass.
                        }
                        else
                        {
                            if (phase == "set-selected") { partition = true; }
                            if (phase == "set-selected-end") { partition = false; }
                            if (phase == "set-append" && mutant != 0) { retained.Add(((object?[])value)[0]!); }
                            if (phase != "set-append" && !(shape == 2 && phase == "set-decoded" && partition)) { return; }
                        }
                        if (++visits % (inner ? 64 : 512) != 0) { return; }
                        samples++; long live = readHeap(); peak = Math.Max(peak, live);
                        if (shape == 2 && phase == "set-decoded")
                        { partitionSamples++; partitionPeak = Math.Max(partitionPeak, live); }
                    };
                    SqlPlanExecutor.ObserveHashSpill = (phase, _) =>
                    {
                        if (shape != 0 || phase is not ("batch-live" or "ordered")) { return; }
                        long live = readHeap(); peak = Math.Max(peak, live);
                        if (phase == "batch-live") { replayPeak = Math.Max(replayPeak, live); replaySamples++; }
                        else { orderedPeak = Math.Max(orderedPeak, live); orderedSamples++; }
                    };
                    try
                    {
                        before = readHeap(); peak = before;
                        // Act: callbacks sample while the real spool/partition owns its workspace.
                        await WarmAsync(populations[population]);
                        long after = readHeap();
                        long measured = peak - Math.Min(before, after);
                        // Assert: every execution must exercise a known live workspace.
                        samples.ShouldBeGreaterThan(0);
                        if (shape == 0) { replaySamples.ShouldBeGreaterThan(0); orderedSamples.ShouldBeGreaterThan(0); }
                        if (inner) { appendSamples.ShouldBe(1); }
                        if (shape == 2) { partitionSamples.ShouldBeGreaterThan(0); }
                        long baseline = Math.Min(before, after);
                        _output.WriteLine($"SET_INNER_HEAP|shape={shape}|population={populations[population]}|mutant={mutant}|execution={execution}|peak={measured}|floor={floor}|G={grant}"
                            + $"|replay={(replaySamples == 0 ? 0 : replayPeak - baseline)}|ordered={(orderedSamples == 0 ? 0 : orderedPeak - baseline)}"
                            + $"|append={(appendSamples == 0 ? 0 : appendPeak - baseline)}|decoded={(partitionSamples == 0 ? 0 : partitionPeak - baseline)}");
                        GC.KeepAlive(retained);
                        return new SetHeapReading(measured, before, after, replayPeak, orderedPeak, appendPeak, partitionPeak);
                    }
                    finally { SqlRowCollector.ObserveBuffer = null; SqlPlanExecutor.ObserveHashSpill = null; }
                });
                rounds[population].Add(sampled);
                await batch.CheckAsync(() =>
                {
                    var value = sampled.Value;
                    long baseline = Math.Min(value.Before, value.After);
                    return [Floor("set/inner peak floor", value.Measured, floor, sampled.Term),
                        ..(shape == 0 ? new[] { Floor("set replay floor", value.ReplayPeak - baseline, floor, sampled.Term),
                            Floor("set ordered floor", value.OrderedPeak - baseline, 8192, sampled.Term) } : []),
                        ..(inner ? new[] { Floor("inner append floor", value.AppendPeak - baseline, 16384 + 8192, sampled.Term) } : []),
                        ..(shape == 2 ? new[] { Floor("set partition floor", value.PartitionPeak - baseline, floor, sampled.Term) } : [])];
                }, () =>
                {
                    var value = sampled.Value;
                    long measured = value.Measured, before = value.Before, after = value.After;
                    long replayPeak = value.ReplayPeak, orderedPeak = value.OrderedPeak, appendPeak = value.AppendPeak, partitionPeak = value.PartitionPeak;
                    measured.ShouldBeGreaterThanOrEqualTo(floor);
                    if (shape == 0)
                    {
                        (replayPeak - Math.Min(before, after)).ShouldBeGreaterThanOrEqualTo(floor);
                        (orderedPeak - Math.Min(before, after)).ShouldBeGreaterThanOrEqualTo(8192);
                    }
                    if (inner) { (appendPeak - Math.Min(before, after)).ShouldBeGreaterThanOrEqualTo(16384 + 8192); }
                    if (shape == 2) { (partitionPeak - Math.Min(before, after)).ShouldBeGreaterThanOrEqualTo(floor); }
                });
                minimum = Math.Min(minimum, sampled.Value.Measured);
            }
            peaks[population] = minimum;
        }
        // Growth slack is identical for both paths and both mutants. Neither bound
        // is adjusted from a failed run; the real-path mutant must fail it.
        long Peak(int p) => rounds[p].Min(x => x.Value.Measured);
        await batch.CheckAsync(() =>
        {
            Term small = Term.Maximum(rounds[0]), large = Term.Maximum(rounds[1]);
            return mutant == 0
                ? [Ceiling("small set/inner peak", Peak(0), grant + 65536, small),
                    Ceiling("large set/inner peak", Peak(1), grant + 65536, large),
                    Ceiling("set/inner growth", Peak(1) - Peak(0), 131072, small + large)]
                : [Floor("set/inner mutant violation", Math.Max(Math.Max(Peak(0) - grant - 65536, Peak(1) - grant - 65536), Peak(1) - Peak(0) - 131072),
                    0, small + large, true)];
        }, () =>
        {
            peaks[0] = Peak(0); peaks[1] = Peak(1);
            bool rejected = peaks[0] > grant + 65536 || peaks[1] > grant + 65536 || peaks[1] - peaks[0] > 131072;
            rejected.ShouldBe(mutant != 0);
        });
        await batch.CompleteAsync();

        async Task WarmAsync(int count)
        {
            string sql = shape switch
            {
                1 => string.Concat("SELECT o.id FROM outer_rows o JOIN live t ON t.id<o.id*0 WHERE o.id<", count.ToString()),
                2 => string.Concat("SELECT id FROM live WHERE id<", count.ToString(), " INTERSECT SELECT id FROM live WHERE id<", count.ToString()),
                3 => string.Concat("SELECT id FROM live WHERE id<", count.ToString(), " UNION ALL SELECT id FROM live WHERE id<", count.ToString()),
                _ => string.Concat("SELECT id FROM live WHERE id<", count.ToString(), " UNION SELECT id FROM live WHERE id<", count.ToString()),
            };
            await using (var result = (DatabaseResultSet)await session.ExecuteAsync(sql)) { }
            session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
            TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        }
    }
    private sealed record SetHeapReading(long Measured, long Before, long After, long ReplayPeak, long OrderedPeak, long AppendPeak, long PartitionPeak);

}
