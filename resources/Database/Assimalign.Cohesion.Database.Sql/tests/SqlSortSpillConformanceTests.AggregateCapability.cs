using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    public static IEnumerable<object[]> OriginalAggregateBoundaries()
    {
        // Original formula: 128 per group plus 128 per ordinary aggregate;
        // DISTINCT/ordered: 320 + 128 * order terms + 32 * argument collations.
        const long key = 32 + 24 + 8 + 32;
        const long orderedOutput = 32 + (24 + 3 * 8 + 3 * 32) + (24 + 8 + 32);
        const long ordinary = 2 * (key + 128 + 2 * 128 + orderedOutput);
        const long distinctEntry = 64 + 2 * (24 + 8 + 32);
        const long distinct = 2 * (key + 128 + 128 + (320 + 32) + orderedOutput) + 3 * distinctEntry;
        const long orderedInput = 32 + 24 + 2 * 8 + 2 * 32;
        // Ordered-only calls have no DISTINCT argument collations. The first two-row
        // group's stable sort overlaps the retained inputs and both sort-key/output slots.
        const long orderedState = 2 * (key + 128 + 128 + (320 + 128));
        const long sortKey = 64 + 24 + 8 + 32;
        long ordered = orderedState + 3 * orderedInput
            + Math.Max(2 * (sortKey + orderedInput), 2 * orderedOutput);
        foreach (string function in new[] { "SUM", "AVG", "MIN", "MAX" })
        {
            yield return [$"SELECT a,COUNT(*),{function}(b) FROM t GROUP BY a ORDER BY a", ordinary];
            yield return [$"SELECT {function}(b) FROM t", 128L + 128 + (32 + 24 + 8 + 32)];
        }
        foreach (string function in new[] { "SUM", "MIN", "MAX" })
        { yield return [$"SELECT a,COUNT(*),{function}(DISTINCT b) FROM t GROUP BY a ORDER BY a", distinct]; }
        yield return ["SELECT a,COUNT(*),MAX(b ORDER BY b) FROM t GROUP BY a ORDER BY a", ordered];
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Aggregate capability: original formula completes at its boundary and refuses one byte below (#1789)")]
    [MemberData(nameof(OriginalAggregateBoundaries))]
    public async Task ExecuteAsync_OriginalAggregateBoundary_ShouldPreserveCapability(string sql, long boundary)
    {
        // Arrange: all comparisons use no temp space and therefore no temp quota.
        await using var fitting = SqlDatabaseEngine.Create("aggregate-fit", new() { StatementMemoryBudget = 67108864 });
        await using var expectedSession = await (await fitting.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Setup(expectedSession);
        await using var expected = (DatabaseResultSet)await expectedSession.ExecuteAsync(sql);
        var expectedRows = await Rows(expected);
        foreach (long grant in new[] { boundary, boundary - 1 })
        {
            var builder = SqlDatabaseEngine.CreateBuilder("aggregate-boundary");
            builder.Options.StatementMemoryBudget = grant;
            await using var engine = await builder.BuildAsync(CancellationToken.None);
            await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
            await Setup(session);
            // Act / Assert
            if (grant == boundary)
            {
                await using var actual = (DatabaseResultSet)await session.ExecuteAsync(sql);
                (await Rows(actual)).ShouldBe(expectedRows);
                AssertColumnMetadata(actual, expected);
                session.LastStatementMetrics!.PeakHashMemoryBytes.ShouldBe(boundary);
            }
            else
            {
                var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql));
                failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS05067");
            }
            session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
            _output.WriteLine($"AGGREGATE_BOUNDARY|sql={sql}|grant={grant}|quota=none|completes={grant == boundary}|peak={session.LastStatementMetrics.PeakHashMemoryBytes}");
        }

        static async Task Setup(SqlDatabaseSession session)
        {
            await session.ExecuteAsync("CREATE TABLE t(a INT,b INT)");
            await session.ExecuteAsync("INSERT INTO t VALUES(1,2),(1,3),(2,4)");
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Large MIN capability: original group and output charges complete without handoff (#1789)")]
    [InlineData(120000, 67108864L, false)]
    [InlineData(2000, 1048576L, true)]
    public async Task ExecuteAsync_OriginalMinGroups_ShouldCompleteInMemory(int groups, long grant, bool temp)
    {
        // Arrange: G=64 MiB/no temp; G=1 MiB/Q=64 MiB with temp configured.
        var expectedRows = Array.Empty<RowValues>();
        DatabaseResultSet? metadata = null;
        int handoffs = 0;
        try
        {
            SqlPlanExecutor.ObserveHashSpill = (phase, _) => { if (phase is "input" or "join-handoff") { handoffs++; } };
            foreach (long budget in new[] { 134217728L, grant })
            {
                var builder = SqlDatabaseEngine.CreateBuilder("min-capability");
                builder.Options.StatementMemoryBudget = budget;
                builder.Options.StatementTempSpaceQuota = temp ? 67108864 : null;
                if (temp) { builder.Options.TempDirectory = System.IO.Path.Combine(_root, "min-" + budget); }
                await using var engine = await builder.BuildAsync(CancellationToken.None);
                await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
                await session.ExecuteAsync("CREATE TABLE g(k INT,v INT)");
                for (int first = 0; first < groups; first += 1000)
                { await session.ExecuteAsync("INSERT INTO g VALUES" + string.Join(',', Enumerable.Range(first, Math.Min(1000, groups - first)).Select(i => $"({i},{i})"))); }

                // Act
                await using var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT k,MIN(v) FROM g GROUP BY k");
                var rows = await Rows(result);
                if (budget != grant) { expectedRows = rows; metadata = result; }
                else
                {
                    // Assert: 96 key + 128 group + 128 state + 136 output per group.
                    rows.ShouldBe(expectedRows); rows.Length.ShouldBe(groups);
                    AssertColumnMetadata(result, metadata!);
                    session.LastStatementMetrics!.PeakHashMemoryBytes.ShouldBe(groups * (96L + 128 + 128 + 136));
                    SqlHashSpillMetrics.For(session.LastStatementMetrics).ShouldBeNull();
                    handoffs.ShouldBe(0);
                    if (temp) { TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0); }
                    _output.WriteLine($"MIN_CAPABILITY|groups={groups}|grant={grant}|quota={(temp ? "67108864" : "none")}|peak={session.LastStatementMetrics.PeakHashMemoryBytes}|handoffs={handoffs}");
                }
            }
        }
        finally { SqlPlanExecutor.ObserveHashSpill = null; }
    }

    private static void AssertColumnMetadata(DatabaseResultSet actual, DatabaseResultSet expected)
        => actual.Columns.Select(c => (c.Ordinal, c.Name, c.Type, c.MaxLength, c.IsNullable))
            .ShouldBe(expected.Columns.Select(c => (c.Ordinal, c.Name, c.Type, c.MaxLength, c.IsNullable)));
}
