using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection("SqlIdentityConcurrency")]
public sealed partial class SqlSequenceFifthPassTests
{
    private readonly ITestOutputHelper _output;

    public SqlSequenceFifthPassTests(ITestOutputHelper output) { _output = output; }

    public static IEnumerable<object[]> GroupingWindows()
    {
        foreach (string grouping in new[] { "ROLLUP(k)", "CUBE(k)", "GROUPING SETS ((k), ())", "k" })
        {
            foreach (bool ordered in new[] { false, true })
            {
                foreach (bool having in new[] { false, true })
                {
                    foreach (int window in new[] { 0, 1, 2 })
                    { yield return new object[] { grouping, ordered, having, window }; }
                }
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Grouping sets: sequence row scopes, HAVING and ordered/unordered windows share projection rules")]
    [MemberData(nameof(GroupingWindows))]
    public async Task Execute_GroupingSequenceWindows_ShouldNumberReachedRows(string grouping, bool ordered, bool having, int window)
    {
        // Arrange: GROUPING() also selects the grouping-set executor for plain GROUP BY.
        await using var engine = SqlDatabaseEngine.Create("fifth-pass", new());
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await Exec(session, "CREATE SEQUENCE s");
        await Exec(session, "CREATE SEQUENCE v");
        await Exec(session, "CREATE TABLE t(k INT)");
        await Exec(session, "INSERT INTO t VALUES (3),(1),(2)");
        string clauses = " FROM t WHERE k>0 GROUP BY " + grouping + (having ? " HAVING SUM(k)>1" : "");
        string order = ordered ? " ORDER BY g, k" : "";
        string page = window == 0 ? "" : window == 1 ? " LIMIT 1 OFFSET 1" : " LIMIT 2";
        int groups = (grouping == "k" ? 3 : 4) - (having ? 1 : 0);
        int offset = window == 1 ? 1 : 0;
        int projected = window == 0 ? groups : 2;
        var ordinary = await Rows(session, "SELECT k, GROUPING(k) g, SUM(k)" + clauses + order);
        int retained = 0;
        SqlPlanExecutor.SequenceCandidatesRetained = count => retained = Math.Max(retained, count);
        try
        {
            // Act
            var rows = await Rows(session, "SELECT nextval('s'), NEXT VALUE FOR v, NEXT VALUE FOR v, k, GROUPING(k) g, SUM(k)" + clauses + order + page);

            // Assert: OFFSET rows allocate too; rejected groups and rows beyond LIMIT do not.
            rows.Length.ShouldBe(projected - offset);
            for (int i = 0; i < rows.Length; i++)
            {
                rows[i][0].ShouldBe((long)(offset + i + 1));
                rows[i][1].ShouldBe((long)(offset + i + 1));
                rows[i][2].ShouldBe(rows[i][1]);
                rows[i][3..].ShouldBe(ordinary[offset + i]);
            }
            retained.ShouldBe(ordered ? projected : 0);
            (await Rows(session, "SELECT nextval('s'), nextval('v')"))[0].ShouldBe(new object?[] { (long)projected + 1, (long)projected + 1 });
            _output.WriteLine($"grouping={grouping}; ordered={ordered}; having={having}; offset={offset}; projected={projected}; delivered={rows.Length}; next={projected + 1}; retained={retained}");

            // A sequence used only by HAVING still needs a fresh row scope per group.
            await Exec(session, "ALTER SEQUENCE v RESTART WITH 1");
            var predicateRows = await Rows(session, "SELECT k, GROUPING(k) FROM t GROUP BY " + grouping +
                " HAVING NEXT VALUE FOR v>1 ORDER BY k");
            int allGroups = grouping == "k" ? 3 : 4;
            predicateRows.Length.ShouldBe(allGroups - 1);
            (await Rows(session, "SELECT nextval('v')"))[0][0].ShouldBe((long)allGroups + 1);
        }
        finally { SqlPlanExecutor.SequenceCandidatesRetained = null; }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Empty grouping sets: duplicate empty groups have separate standard sequence row scopes")]
    public async Task Execute_EmptyGroupingSets_ShouldAllocateForEachEmptyGroup()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("fifth-pass", new());
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await Exec(session, "CREATE SEQUENCE s");
        await Exec(session, "CREATE TABLE t(k INT)");

        // Act
        var rows = await Rows(session, "SELECT NEXT VALUE FOR s, COUNT(*), GROUPING(k) FROM t GROUP BY GROUPING SETS ((k), (), ())");

        // Assert
        rows.Length.ShouldBe(2);
        rows[0].ShouldBe(new object?[] { 1L, 0L, 1 });
        rows[1].ShouldBe(new object?[] { 2L, 0L, 1 });
        (await Rows(session, "SELECT nextval('s')"))[0][0].ShouldBe(3L);
    }

    private static async Task Exec(SqlDatabaseSession session, string sql)
    {
        var result = await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
        result.Diagnostics.ShouldBeEmpty();
    }

    private static async Task<object?[][]> Rows(SqlDatabaseSession session, string sql)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        return rows.ToArray();
    }
}
