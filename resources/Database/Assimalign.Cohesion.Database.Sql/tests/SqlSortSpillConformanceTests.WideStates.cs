using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using static Assimalign.Cohesion.Database.Sql.Tests.TestObjects.SqlHeapPool;
using static Assimalign.Cohesion.Database.Sql.Tests.TestObjects.SqlHeapAcceptance;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    // MIN(INT) samples the heap at every hash spill transition, each sample two forced collections.
    // Its samples are split across this many cases, which together sample every transition, so a case
    // that needs all four remeasurement attempts stays inside CI's five-minute hang timeout (#1883).
    private const int WideTransitionShards = 4;

    public static IEnumerable<object[]> WideHashStates()
    {
        foreach (long grant in new[] { 1048576L, 4194304L })
        {
            foreach (string function in new[] { "MIN", "MAX", "SUM", "AVG", "COUNT", "BOOL_AND" })
            {
                foreach (string type in new[] { "INT", "BIGINT", "DOUBLE PRECISION", "NUMERIC(12,2)", "DATE" })
                {
                    int shards = function == "MIN" && type == "INT" ? WideTransitionShards : 1;
                    for (int shard = 0; shard < shards; shard++) { yield return [grant, function, type, shard]; }
                }
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Sixty-four native states: phase samples stay within 1 and 4 MiB grants (#1789)")]
    [MemberData(nameof(WideHashStates))]
    public async Task ExecuteAsync_WideNativeStates_ShouldStayWithinGrant(long grant, string function, string type, int shard)
    {
        // Arrange: BOOL_AND receives a predicate over each input family; SUM/AVG over
        // DATE are language errors, and floating sums have no static spill magnitude proof.
        await using var run = await CreateCorpusEngineAsync(grant, true, 12000);
        string arguments = string.Join(',', Enumerable.Range(1, 64).Select(i =>
        {
            string value = type == "DATE"
                ? "CAST(CASE WHEN k%2=0 THEN '" + new DateOnly(2026, 1, 1).AddDays(i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) +
                  "' ELSE '" + new DateOnly(2025, 1, 1).AddDays(i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "' END AS DATE)"
                : $"CAST(k+{i} AS {type})";
            return function + "(" + value + (function == "BOOL_AND" ? " IS NOT NULL" : "") + ")";
        }));
        string sql = "SELECT id," + arguments + " FROM t GROUP BY id";
        await ExecuteCorpusAsync(run, sql); // warm parsing, transitions, readers and output

        using var pool = new HashPoolEvents();
        using var images = new WindowPoolLease();
        images.Drain(pool);

        // Act
        var batch = new Batch(pool, _output, nameof(ExecuteAsync_WideNativeStates_ShouldStayWithinGrant));
        bool everyTransition = function == "MIN" && type == "INT";
        var sampled = await batch.MeasureAsync($"function={function}|type={type}|grant={grant}|shard={shard}",
            readHeap => ExecuteCorpusAsync(run, sql, sample: true, everyTransition: everyTransition, images: images, pool: pool, readHeap: readHeap,
                transitions: everyTransition ? (shard, WideTransitionShards) : null));

        // Assert
        await batch.CheckAsync(() => [Ceiling("wide-state excess", sampled.Value.Live - grant - 262144, 0, sampled.Term)], () =>
        {
            var actual = sampled.Value;
            long excess = actual.Live - grant - 262144;
            _output.WriteLine($"WIDE_STATES|{function}|{type}|grant={grant}|shard={shard}|live={actual.Live}|excess={excess}|levels={actual.Passes}|rows={actual.Rows.Length}|error={actual.Error}");
            excess.ShouldBeLessThanOrEqualTo(0);
            if (function is "SUM" or "AVG" && type == "DATE")
            {
                actual.Error.ShouldStartWith("CS01006|");
            }
            else if (function is "SUM" or "AVG" && type == "DOUBLE PRECISION")
            {
                actual.Error.ShouldStartWith("CS05067|");
            }
            else
            {
                actual.Error.ShouldBeNull();
                actual.Rows.Length.ShouldBe(12000);
                actual.Passes.ShouldBeGreaterThan(0);
            }
        });
        await batch.CompleteAsync();
    }
}
