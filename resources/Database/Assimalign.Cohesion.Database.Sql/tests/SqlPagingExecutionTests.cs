using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlPagingExecutionTests
{
    private static readonly string[] _setup =
    [
        "CREATE TABLE t (id INT, category INT, name TEXT)",
        "INSERT INTO t VALUES (40, 2, 'd'), (10, 1, 'a'), (50, 2, 'e'), (20, 3, 'b'), (30, 1, 'c')",
        "CREATE TABLE peers (id INT)",
        "INSERT INTO peers VALUES (30), (50), (20), (40), (10)",
    ];

    [Theory(DisplayName = "Cohesion Test [Sql] - Paging: every spelling returns the sorted window across plan shapes")]
    [InlineData("SELECT id FROM t", "id", "10,20,30,40,50")]
    [InlineData("SELECT t.id FROM t INNER JOIN peers p ON t.id = p.id", "t.id", "10,20,30,40,50")]
    [InlineData("SELECT category FROM t GROUP BY category", "category", "1,2,3")]
    [InlineData("SELECT DISTINCT category FROM t", "category", "1,2,3")]
    [InlineData("SELECT ORDINAL_POSITION FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 't'", "ORDINAL_POSITION DESC", "3,2,1")]
    public async Task ExecuteAsync_PagingPlanShape_ShouldReturnExactWindow(string query, string order, string expectedText)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("paging", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        foreach (string sql in _setup) { await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None); }
        long[] expected = expectedText.Split(',').Select(long.Parse).ToArray();
        var scan = await ValuesAsync(session, query);
        scan.SequenceEqual(expected).ShouldBeFalse("RequireReordering: the expected sort must differ from the actual scan.");
        long[] insertion = order.Contains("category") ? [2, 1, 3] : order.Contains("ORDINAL") ? [1, 2, 3] : [40, 10, 50, 20, 30];
        insertion.SequenceEqual(expected).ShouldBeFalse("RequireReordering: insertion order must not satisfy the expected sort.");
        (string Prefix, string Tail, long[] Rows)[] windows =
        [
            ("", "LIMIT 2 OFFSET 1", expected.Skip(1).Take(2).ToArray()),
            ("", "OFFSET 1 LIMIT 2", expected.Skip(1).Take(2).ToArray()),
            ("", "OFFSET 1 ROWS FETCH NEXT 2 ROWS ONLY", expected.Skip(1).Take(2).ToArray()),
            ("", "OFFSET 2 ROWS FETCH NEXT 3 ROWS ONLY", expected.Skip(2).Take(3).ToArray()),
            ("", "FETCH NEXT 2 ROWS ONLY OFFSET 1 ROW", expected.Skip(1).Take(2).ToArray()),
            ("", "OFFSET 1 ROW FETCH FIRST 2 ROW ONLY", expected.Skip(1).Take(2).ToArray()),
            ("", "FETCH FIRST ROW ONLY", expected.Take(1).ToArray()),
            ("", "FETCH FIRST 0 ROWS ONLY", []),
            ("TOP (2) ", "", expected.Take(2).ToArray()),
            ("TOP 2 ", "", expected.Take(2).ToArray()),
            ("", "OFFSET 1 ROW", expected.Skip(1).ToArray()),
        ];

        // Act / Assert
        foreach (var (prefix, tail, rows) in windows)
        {
            string paged = query.StartsWith("SELECT DISTINCT ", StringComparison.Ordinal)
                ? query.Replace("SELECT DISTINCT ", "SELECT DISTINCT " + prefix, StringComparison.Ordinal)
                : "SELECT " + prefix + query[7..];
            (await ValuesAsync(session, $"{paged} ORDER BY {order} {tail}")).ToArray().ShouldBe(rows, paged + tail);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Paging: subqueries, derived bodies, CTEs, parameters and INSERT sources execute")]
    public async Task ExecuteAsync_PagingChildren_ShouldApplyTheirOwnWindows()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("paging-children", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        foreach (string sql in _setup) { await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None); }

        // Act / Assert
        (await ValuesAsync(session, "SELECT id FROM t WHERE id IN (SELECT id FROM t ORDER BY id FETCH FIRST 2 ROWS ONLY) ORDER BY id"))
            .ShouldBe(new long[] { 10, 20 });
        (await ValuesAsync(session, "SELECT (SELECT TOP (1) id FROM t ORDER BY id) FROM peers LIMIT 1")).ShouldBe(new long[] { 10 });
        (await ValuesAsync(session, "SELECT id FROM t WHERE EXISTS (SELECT TOP 0 id FROM peers) ORDER BY id")).ShouldBeEmpty();
        (await ValuesAsync(session, "SELECT id FROM t FETCH NEXT 2 ROWS ONLY")).Count.ShouldBe(2);
        (await ValuesAsync(session, "SELECT * FROM (SELECT TOP 2 id FROM t ORDER BY id) d")).ShouldBe(new long[] { 10, 20 });
        (await ValuesAsync(session, "WITH c AS (SELECT id FROM t ORDER BY id OFFSET 1 FETCH FIRST 2 ROWS ONLY) SELECT * FROM c"))
            .ShouldBe(new long[] { 20, 30 });
        var parameters = new Dictionary<string, object?> { ["1"] = 2, ["n"] = 2 };
        (await ValuesAsync(session, "SELECT id FROM t ORDER BY id FETCH FIRST $1 ROWS ONLY", parameters)).ShouldBe(new long[] { 10, 20 });
        (await ValuesAsync(session, "SELECT DISTINCT TOP (@n) category FROM t ORDER BY category", parameters)).ShouldBe(new long[] { 1, 2 });
        await session.ExecuteAsync("CREATE TABLE u (id INT)", cancellationToken: CancellationToken.None);
        var inserted = await session.ExecuteAsync("INSERT INTO u SELECT TOP (2) id FROM t ORDER BY id", cancellationToken: CancellationToken.None);
        inserted.AffectedCount.ShouldBe(2);
        (await ValuesAsync(session, "SELECT id FROM u ORDER BY id")).ShouldBe(new long[] { 10, 20 });
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Paging: count validation names the written clause across planner paths")]
    [InlineData("SELECT id FROM t ORDER BY id FETCH FIRST -1 ROWS ONLY", "FETCH")]
    [InlineData("SELECT TOP (-1) id FROM t ORDER BY id", "TOP")]
    [InlineData("SELECT id FROM t OFFSET -1 ROWS", "OFFSET")]
    [InlineData("SELECT category FROM t GROUP BY category FETCH FIRST -1 ROWS ONLY", "FETCH")]
    [InlineData("SELECT TOP (-1) t.id FROM t INNER JOIN peers p ON t.id = p.id", "TOP")]
    [InlineData("SELECT TOP (-1) TABLE_NAME FROM INFORMATION_SCHEMA.TABLES", "TOP")]
    [InlineData("WITH c AS (SELECT id FROM t) SELECT TOP (-1) id FROM c", "TOP")]
    [InlineData("WITH c AS (SELECT TOP (-1) 1 AS id) SELECT * FROM c", "TOP")]
    [InlineData("WITH c AS (SELECT 1 AS id FETCH FIRST -1 ROW ONLY) SELECT * FROM c", "FETCH")]
    public async Task ExecuteAsync_InvalidCount_ShouldNameItsSyntax(string sql, string clause)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("paging-counts", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        foreach (string setup in _setup) { await session.ExecuteAsync(setup, cancellationToken: CancellationToken.None); }

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None));

        // Assert
        error.Message.ShouldBe($"{clause} requires a non-negative integer.");
        error.Diagnostics.ShouldContain(item => item.Code == "CS02018");
    }

    private static async Task<List<long>> ValuesAsync(SqlDatabaseSession session, string sql, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        await using var result = (await session.ExecuteAsync(sql, parameters, CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        var values = new List<long>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { values.Add(Convert.ToInt64(row.GetValue(0))); }
        return values;
    }
}
