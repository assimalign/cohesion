using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    public static IEnumerable<object[]> WideHashStates()
    {
        foreach (long grant in new[] { 1048576L, 4194304L })
        {
            foreach (string function in new[] { "MIN", "MAX", "SUM", "AVG", "COUNT", "BOOL_AND" })
            {
                foreach (string type in new[] { "INT", "BIGINT", "DOUBLE PRECISION", "NUMERIC(12,2)", "DATE" })
                { yield return [grant, function, type]; }
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Sixty-four native states: phase samples stay within 1 and 4 MiB grants (#1789)")]
    [MemberData(nameof(WideHashStates))]
    public async Task ExecuteAsync_WideNativeStates_ShouldStayWithinGrant(long grant, string function, string type)
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

        // Act
        var actual = await ExecuteCorpusAsync(run, sql, sample: true, everyTransition: function == "MIN" && type == "INT");

        // Assert
        long excess = actual.Live - grant - 262144;
        _output.WriteLine($"WIDE_STATES|{function}|{type}|grant={grant}|live={actual.Live}|excess={excess}|levels={actual.Passes}|rows={actual.Rows.Length}|error={actual.Error}");
        excess.ShouldBeLessThanOrEqualTo(0);
        if (function is "SUM" or "AVG" && type == "DATE") { actual.Error.ShouldStartWith("CS01006|"); }
        else if (function is "SUM" or "AVG" && type == "DOUBLE PRECISION") { actual.Error.ShouldStartWith("CS05067|"); }
        else { actual.Error.ShouldBeNull(); actual.Rows.Length.ShouldBe(12000); actual.Passes.ShouldBeGreaterThan(0); }
    }
}
