using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - During-operation set/inner heap bounds reject per-input boxes and one kept decoded row per pass (#1789)")]
    [InlineData(false)] [InlineData(true)]
    public async Task ExecuteAsync_SetInnerLiveHeap_ShouldRejectRetentionMutant(bool inner)
    {
        // Arrange: a separate baseline for every execution, with reusable pool images
        // rooted before sampling. The allowance beyond G is fixed at 64 KiB.
        long grant = inner ? 32768 : 262144;
        long floor = inner ? 8192 : 65536;
        int[] populations = inner ? [256, 4096] : [8192, 32768];
        await using var engine = await Create("set-inner-live-" + inner, grant, quota: 134217728);
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
        await WarmAsync(populations[1]); images.Drain(pool); await QuiesceWindowPoolAsync(pool);
        var peaks = new long[2, 2];
        for (int population = 0; population < 2; population++)
        {
            await WarmAsync(populations[population]);
            for (int mutant = 0; mutant < 2; mutant++)
            {
                long minimum = long.MaxValue;
                for (int execution = 0; execution < 3; execution++)
                {
                    images.Drain(pool);
                    var retained = new List<object>();
                    long peak = 0, before = 0;
                    int visits = 0, samples = 0;
                    SqlRowCollector.ObserveBuffer = (phase, value) =>
                    {
                        if (inner)
                        {
                            if (phase != "inner-decoded" || ((object?[])value)[0] is not int id || id != 0) { return; }
                            if (mutant != 0) { retained.Add(value); } // One decoded row per actual pass.
                        }
                        else
                        {
                            if (phase != "set-append") { return; }
                            if (mutant != 0) { retained.Add(((object?[])value)[0]!); }
                        }
                        if (++visits % (inner ? 64 : 512) != 0) { return; }
                        samples++; peak = Math.Max(peak, LiveHeap.Read());
                    };
                    try
                    {
                        before = LiveHeap.Read(); peak = before;
                        // Act: callbacks sample while the real spool/partition owns its workspace.
                        await WarmAsync(populations[population]);
                        long after = LiveHeap.Read();
                        long measured = peak - Math.Min(before, after);
                        // Assert: every execution must exercise a known live workspace.
                        samples.ShouldBeGreaterThan(0); measured.ShouldBeGreaterThanOrEqualTo(floor);
                        minimum = Math.Min(minimum, measured);
                        _output.WriteLine($"SET_INNER_HEAP|inner={inner}|population={populations[population]}|mutant={mutant}|execution={execution}|peak={measured}|floor={floor}|G={grant}");
                        GC.KeepAlive(retained);
                    }
                    finally { SqlRowCollector.ObserveBuffer = null; }
                }
                peaks[mutant, population] = minimum;
            }
        }
        // Growth slack is identical for both paths and both mutants. Neither bound
        // is adjusted from a failed run; the real-path mutant must fail it.
        for (int mutant = 0; mutant < 2; mutant++)
        {
            bool rejected = peaks[mutant, 0] > grant + 65536 || peaks[mutant, 1] > grant + 65536
                || peaks[mutant, 1] - peaks[mutant, 0] > 131072;
            rejected.ShouldBe(mutant != 0);
        }

        async Task WarmAsync(int count)
        {
            string sql = inner
                ? $"SELECT o.id FROM outer_rows o JOIN live t ON t.id<o.id*0 WHERE o.id<{count}"
                : $"SELECT id FROM live WHERE id<{count} UNION SELECT id FROM live WHERE id<{count}";
            await using var result = (DatabaseResultSet)await session.ExecuteAsync(sql);
            session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0); TempBytes(engine.TempSpace!).ShouldBeGreaterThanOrEqualTo(0);
        }
    }
}
