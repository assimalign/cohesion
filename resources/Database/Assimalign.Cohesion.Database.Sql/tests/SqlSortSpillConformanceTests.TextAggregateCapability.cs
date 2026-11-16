using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    public static IEnumerable<object[]> OriginalTextAggregateBoundaries()
    {
        const long key = 32 + 24 + 8 + 32;
        const long row = 32 + 24 + 2 * 8 + 32; // one scalar and one shared wide value
        const int groups = 8192, length = 2048;
        const long wideValue = (24L + 2 * length + 7) & ~7L;
        const long sharedWide = wideValue + 64; // reference-identity entry
        const long min = groups * (key + 128 + 128 + row) + sharedWide - 32 + sharedWide;
        // The final group's stable sort overlaps prior output, a key and a sorted-input row.
        const long ordered = groups * (key + 128 + (320 + 128) + row + row)
            + 3 * sharedWide - 32 + (row + sharedWide) + (64 + 24 + 8 + 32) - row;
        foreach (bool temp in new[] { false, true })
        {
            foreach (string function in new[] { "MIN", "MAX" })
            {
                int characters = function == "MIN" ? 5 : 4; // alpha / beta
                long value = (24L + 2 * characters + 7) & ~7L;
                yield return [false, false, function, temp, 128L + 128 + Math.Max(0, value - 32) + 32 + 24 + 8 + value];
            }
            yield return [true, false, "MIN", temp, min];
            yield return [true, true, "MAX", temp, ordered];
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Text aggregate capability keeps original formula boundaries with and without temp space (#1789)")]
    [MemberData(nameof(OriginalTextAggregateBoundaries))]
    public async Task ExecuteAsync_OriginalTextAggregateBoundary_ShouldMatchFitting(bool wide, bool ordered, string function, bool temp, long boundary)
    {
        // Arrange: both configurations use the same quota as their fitting reference.
        RowValues[] expected = [];
        DatabaseResultSet? metadata = null;
        foreach (long grant in new[] { 67108864L, boundary, boundary - 1 })
        {
            var builder = SqlDatabaseEngine.CreateBuilder("text-aggregate-capability");
            builder.Options.StatementMemoryBudget = grant;
            builder.Options.StatementTempSpaceQuota = temp ? 67108864 : null;
            if (temp) { builder.Options.TempDirectory = Path.Combine(_root, "text-" + grant); }
            await using var engine = await builder.BuildAsync();
            await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
            if (wide)
            {
                await session.ExecuteAsync("CREATE TABLE t(k INT)");
                await session.ExecuteAsync("INSERT INTO t VALUES" + string.Join(',', Enumerable.Range(1, 8192).Select(k => $"({k})")));
            }
            else
            {
                await session.ExecuteAsync("CREATE TABLE a(note TEXT)");
                await session.ExecuteAsync("INSERT INTO a VALUES('alpha'),('beta')");
            }
            string sql = wide ? $"SELECT k,{function}(@p{(ordered ? " ORDER BY k" : "")}) FROM t GROUP BY k" : $"SELECT {function}(note) FROM a";
            var parameters = wide ? new Dictionary<string, object?> { ["p"] = new string('x', 2048) } : null;
            // Act / Assert
            if (grant < boundary)
            {
                var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql, parameters));
                failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS05067");
            }
            else
            {
                await using var result = (DatabaseResultSet)await session.ExecuteAsync(sql, parameters);
                var rows = await Rows(result);
                if (grant == 67108864) { expected = rows; metadata = result; }
                else { rows.ShouldBe(expected); AssertColumnMetadata(result, metadata!); }
                session.LastStatementMetrics!.PeakHashMemoryBytes.ShouldBe(boundary);
                SqlHashSpillMetrics.For(session.LastStatementMetrics).ShouldBeNull();
            }
            session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
            if (temp) { TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0); }
            _output.WriteLine($"TEXT_AGGREGATE_BOUNDARY|sql={sql}|grant={grant}|quota={(temp ? "67108864" : "none")}|completes={grant >= boundary}|boundary={boundary}");
        }
    }
}
