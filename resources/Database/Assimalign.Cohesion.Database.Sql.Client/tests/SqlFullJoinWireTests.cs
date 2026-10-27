using System;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

public sealed class SqlFullJoinWireTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Client] - FULL sends both unmatched bags, coalesced USING values and typed metadata on both transports (#1525)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_FullUsing_ShouldReturnBothUnmatchedBags(bool tcp)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout(30));
        foreach (string sql in new[]
        {
            "CREATE TABLE a(extra TEXT, id DECIMAL, name VARCHAR(8) COLLATE case_insensitive)",
            "CREATE TABLE b(id INT, name VARCHAR(12) COLLATE case_insensitive, tail TEXT)",
            "INSERT INTO a VALUES('L',1,'Ada'),('dup',1,'Ada'),('only',3,'Lin'),('null',NULL,'null')",
            "INSERT INTO b VALUES(1,'ADA','R'),(2,'Grace','only'),(NULL,'null','null')",
        }) { await connection.ExecuteAsync(sql, cancellationToken: SqlClientTestHarness.Timeout(30)); }

        // Act
        var result = await connection.QueryAsync("SELECT *,a.id AS aid,b.id AS bid FROM a FULL OUTER JOIN b USING(name,id) ORDER BY id NULLS LAST,extra NULLS LAST",
            cancellationToken: SqlClientTestHarness.Timeout(30));

        // Assert
        result.Columns.Select(column => column.Name).ShouldBe(new[] { "name", "id", "extra", "tail", "aid", "bid" });
        result.Columns.Select(column => column.Type).ShouldBe(new[] { DatabaseType.String, DatabaseType.Decimal, DatabaseType.String,
            DatabaseType.String, DatabaseType.Decimal, DatabaseType.Int32 });
        result.Columns[0].MaxLength.ShouldBeNull();
        result.Count.ShouldBe(6);
        object?[][] expected = [["Ada", 1m, "L", "R", 1m, 1], ["Ada", 1m, "dup", "R", 1m, 1],
            ["Grace", 2m, null, "only", null, 2], ["Lin", 3m, "only", null, 3m, null],
            ["null", null, "null", null, null, null], ["null", null, null, "null", null, null]];
        for (int i = 0; i < expected.Length; i++)
        {
            Enumerable.Range(0, 6).Select(column => result[i][column]).ShouldBe(expected[i]);
        }
        var explain = await connection.QueryAsync("EXPLAIN ANALYZE SELECT * FROM a FULL JOIN b USING(id)", cancellationToken: SqlClientTestHarness.Timeout(30));
        explain.Single(row => Equals(row[2], "JoinStep"))[5]!.ToString()!.ShouldContain("FullHashJoin; unmatched=both", Case.Sensitive);
        await connection.ExecuteAsync("DELETE FROM a", cancellationToken: SqlClientTestHarness.Timeout(30));
        (await connection.QueryAsync("SELECT id FROM a FULL JOIN b USING(id)", cancellationToken: SqlClientTestHarness.Timeout(30))).Count.ShouldBe(3);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - FULL coded physical and direct-DML refusals leave the connection usable (#1525)")]
    [InlineData(false, "SELECT * FROM users a FULL JOIN users b ON a.id < b.id", "CS05070")]
    [InlineData(true, "SELECT * FROM users a FULL JOIN users b ON a.id < b.id", "CS05070")]
    [InlineData(false, "UPDATE users SET score = 0 FROM users a FULL JOIN users b USING(id)", "CS05069")]
    [InlineData(true, "DELETE FROM users USING users a FULL JOIN users b USING(id)", "CS05069")]
    public async Task QueryAsync_FullRefusal_ShouldPreserveConnection(bool tcp, string query, string code)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout(30));

        // Act
        var error = await Should.ThrowAsync<SqlClientException>(async () => await connection.ExecuteAsync(query, cancellationToken: SqlClientTestHarness.Timeout(30)));

        // Assert
        error.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
        (await connection.QueryAsync("SELECT id FROM users ORDER BY id", cancellationToken: SqlClientTestHarness.Timeout(30))).Select(row => row[0]).ShouldBe(new object?[] { 1, 2 });
        await connection.ExecuteAsync("UPDATE users SET score = 9 FROM (SELECT id FROM users a FULL JOIN users b USING(id)) q WHERE users.id = q.id", cancellationToken: SqlClientTestHarness.Timeout(30));
        (await connection.QueryAsync("SELECT score FROM users ORDER BY id", cancellationToken: SqlClientTestHarness.Timeout(30))).Select(row => row[0]).ShouldBe(new object?[] { 9L, 9L });
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - FULL NULL-key build budget refusal preserves data and wire reuse (#1525)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_FullBudget_ShouldReportMemoryRefusal(bool tcp)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp, configureEngine: options => options.StatementMemoryBudget = 1024);
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout(30));
        await connection.ExecuteAsync("CREATE TABLE nulls(id INT)", cancellationToken: SqlClientTestHarness.Timeout(30));
        await connection.ExecuteAsync("INSERT INTO nulls VALUES " + string.Join(',', Enumerable.Repeat("(NULL)", 100)), cancellationToken: SqlClientTestHarness.Timeout(30));

        // Act
        var error = await Should.ThrowAsync<SqlClientException>(async () => await connection.QueryAsync("SELECT * FROM users FULL JOIN nulls USING(id)", cancellationToken: SqlClientTestHarness.Timeout(30)));

        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS05067");
        (await connection.QueryAsync("SELECT COUNT(*) FROM nulls", cancellationToken: SqlClientTestHarness.Timeout(30))).ShouldHaveSingleItem()[0].ShouldBe(100L);
    }
}
