using System;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Testing;

using static Assimalign.Cohesion.Database.Sql.Tests.TestObjects.SqlHeapPool;
using static Assimalign.Cohesion.Database.Sql.Tests.TestObjects.SqlHeapMeasurement;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Set ordinal append peaks include the live sorter arena (#1789)")]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task ExecuteAsync_SetOrdinalLiveHeap_ShouldBoundAppendPeak(int shape)
    {
        // Arrange: StorageTempSorter owns byte[grant - 32,768] until Complete.
        const long grant = 262144;
        await using var engine = await Create("set-ordinal-live-" + shape, grant, quota: 134217728);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Seed(session, 32768);
        using var pool = new HashPoolEvents();
        using var images = new WindowPoolLease();
        await RunAsync(32768); images.Drain(pool);
        var peaks = new long[2];
        int[] populations = [8192, 32768];
        for (int population = 0; population < populations.Length; population++)
        {
            int count = populations[population];
            await RunAsync(count);
            long minimum = long.MaxValue;
            for (int execution = 0; execution < 3; execution++)
            {
                await MeasureAndAssertAsync(pool, _output, nameof(ExecuteAsync_SetOrdinalLiveHeap_ShouldBoundAppendPeak), $"shape={shape}|population={count}|execution={execution}", async readHeap =>
                {
                    images.Drain(pool);
                    long peak = 0, floor = 0; int appends = 0, samples = 0;
                    SqlPlanExecutor.ObserveHashSpill = (phase, workspace) =>
                    {
                        if (phase != (shape == 0 ? "ordinal-append" : "set-ordinal-append")) { return; }
                        if (++appends != count) { return; }
                        floor = workspace; samples++; peak = readHeap();
                    };
                    try
                    {
                        long before = readHeap();
                        // Act: sample after the final Append, while the sorter arena is live.
                        await RunAsync(count);
                        long after = readHeap();
                        long measured = peak - Math.Min(before, after);
                        // Assert: neither the grant allowance nor the growth slack is raised.
                        appends.ShouldBe(count); samples.ShouldBe(1); floor.ShouldBeGreaterThan(0);
                        return (Action)(() =>
                        {
                            measured.ShouldBeGreaterThanOrEqualTo(floor);
                            measured.ShouldBeLessThanOrEqualTo(grant + 65536);
                            minimum = Math.Min(minimum, measured);
                            _output.WriteLine($"SET_ORDINAL_HEAP|shape={shape}|population={count}|execution={execution}|peak={measured}|floor={floor}|G={grant}");
                        });
                    }
                    finally { SqlPlanExecutor.ObserveHashSpill = null; }
                });
            }
            peaks[population] = minimum;
        }
        (peaks[1] - peaks[0]).ShouldBeLessThanOrEqualTo(131072);

        async Task RunAsync(int count)
        {
            string operation = shape == 0 ? "UNION" : shape == 1 ? "INTERSECT" : "EXCEPT";
            string predicate = shape == 2 ? "id<0" : string.Concat("id<", count.ToString());
            await using (var result = (DatabaseResultSet)await session.ExecuteAsync(
                string.Concat("SELECT id FROM t WHERE id<", count.ToString(), " ", operation, " SELECT id FROM t WHERE ", predicate))) { }
            session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
            TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        }
    }
}
