using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlRightJoinExplainTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Indexed RIGHT EXPLAIN visits the complete physical outer before its inner seek (#1525)")]
    [InlineData("EXPLAIN", "b", "b")]
    [InlineData("EXPLAIN ANALYZE", "b", "b")]
    [InlineData("EXPLAIN", "(SELECT id FROM b) d", "d")]
    [InlineData("EXPLAIN ANALYZE", "(SELECT id FROM b) d", "d")]
    public async Task Explain_IndexedRightJoin_ShouldVisitOuterBeforeInner(string explain, string outerSource, string outerAlias)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("right-explain-order", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        foreach (string sql in new[]
        {
            "CREATE TABLE a (id INT NOT NULL)", "CREATE TABLE b (id INT NOT NULL)",
            "INSERT INTO a VALUES (1)", "INSERT INTO b VALUES (1), (2)", "CREATE INDEX ix_a ON a(id)",
        })
        {
            (await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None)).Status.ShouldBe(DatabaseResultStatus.Success);
        }

        // Act
        await using var result = (await session.ExecuteAsync($"{explain} SELECT * FROM a RIGHT JOIN {outerSource} ON a.id = {outerAlias}.id;",
            cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        var plan = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            plan.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray());
        }

        // Assert
        var step = plan.Single(row => Equals(row[2], "JoinStep"));
        var outer = plan.Single(row => Equals(row[2], "TableScan") && Equals(row[3], "dbo.b"));
        var inner = plan.Single(row => Equals(row[2], "IndexSeek") && Equals(row[3], "dbo.a"));
        inner[1].ShouldBe(step[0]);
        inner[4].ShouldBe("ix_a");
        Convert.ToInt32(outer[0]).ShouldBeLessThan(Convert.ToInt32(inner[0]));
    }
}
