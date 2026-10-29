using System.Collections.Generic;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

public sealed class SqlOnUpdateWireTests
{
    public static IEnumerable<object[]> Refusals()
    {
        foreach (bool tcp in new[] { false, true })
        {
            foreach (string action in new[] { "CASCADE", "SET NULL", "SET DEFAULT" })
            {
                for (int path = 0; path < 4; path++) { yield return [tcp, action, path]; }
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - ON UPDATE: each unimplemented action refuses every update path atomically over InMemory and TCP (#1723)")]
    [MemberData(nameof(Refusals))]
    public async Task ExecuteAsync_UpdateAction_ShouldRefuseAndKeepConnectionUsable(bool tcp, string action, int path)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout(30));
        await connection.ExecuteAsync("CREATE TABLE p(id INT PRIMARY KEY,v INT)", cancellationToken: SqlClientTestHarness.Timeout(30));
        await connection.ExecuteAsync($"CREATE TABLE c(pid INT DEFAULT 1 REFERENCES p(id) ON UPDATE {action} ON DELETE SET NULL)", cancellationToken: SqlClientTestHarness.Timeout(30));
        await connection.ExecuteAsync("INSERT INTO p VALUES(1,0),(2,0)", cancellationToken: SqlClientTestHarness.Timeout(30));
        await connection.ExecuteAsync("INSERT INTO c VALUES(2)", cancellationToken: SqlClientTestHarness.Timeout(30));
        string sql = path switch
        {
            0 => "UPDATE p SET id=CASE WHEN id=2 THEN 20 ELSE id END,v=1",
            1 => "UPDATE p SET id=s.next_id,v=1 FROM (SELECT 1 AS old_id,1 AS next_id UNION ALL SELECT 2,20) s WHERE p.id=s.old_id",
            2 => "INSERT INTO p VALUES(1,1),(2,1) ON CONFLICT(id) DO UPDATE SET id=CASE WHEN p.id=2 THEN 20 ELSE p.id END,v=excluded.v",
            _ => "MERGE INTO p USING (SELECT 3 AS id UNION ALL SELECT 1 UNION ALL SELECT 2) s ON p.id=s.id WHEN MATCHED THEN UPDATE SET id=CASE WHEN p.id=2 THEN 20 ELSE p.id END,v=1 WHEN NOT MATCHED THEN INSERT VALUES(s.id,1)",
        };

        // Act
        var error = await Should.ThrowAsync<SqlClientException>(async () =>
            await connection.ExecuteAsync(sql, cancellationToken: SqlClientTestHarness.Timeout(30)));

        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CK00001");
        error.Message.ShouldContain($"ON UPDATE {action} action execution", Case.Sensitive);
        error.ConnectionUsable.ShouldBeTrue();
        var rows = await connection.QueryAsync("SELECT id,v FROM p ORDER BY id", cancellationToken: SqlClientTestHarness.Timeout(30));
        rows.Count.ShouldBe(2);
        rows[0][0].ShouldBe(1); rows[0][1].ShouldBe(0);
        rows[1][0].ShouldBe(2); rows[1][1].ShouldBe(0);
        var child = await connection.QueryAsync("SELECT pid FROM c", cancellationToken: SqlClientTestHarness.Timeout(30));
        child.ShouldHaveSingleItem()[0].ShouldBe(2);
        var rule = await connection.QueryAsync("SELECT UPDATE_RULE,DELETE_RULE FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS", cancellationToken: SqlClientTestHarness.Timeout(30));
        rule.ShouldHaveSingleItem()[0].ShouldBe(action);
        rule[0][1].ShouldBe("SET NULL");
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - ON UPDATE: self-references refuse every update path over InMemory and TCP (#1723)")]
    [MemberData(nameof(Refusals))]
    public async Task ExecuteAsync_SelfReference_ShouldReportActionRefusal(bool tcp, string action, int path)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout(30));
        await connection.ExecuteAsync($"CREATE TABLE t(id INT PRIMARY KEY,pid INT REFERENCES t(id) ON UPDATE {action})", cancellationToken: SqlClientTestHarness.Timeout(30));
        await connection.ExecuteAsync("INSERT INTO t VALUES(1,1)", cancellationToken: SqlClientTestHarness.Timeout(30));
        string sql = path switch
        {
            0 => "UPDATE t SET id=2",
            1 => "UPDATE t SET id=s.next_id FROM (SELECT 2 AS next_id) s",
            2 => "INSERT INTO t VALUES(1,NULL) ON CONFLICT(id) DO UPDATE SET id=2",
            _ => "MERGE INTO t USING (SELECT 1 AS id) s ON t.id=s.id WHEN MATCHED THEN UPDATE SET id=2",
        };

        // Act
        var error = await Should.ThrowAsync<SqlClientException>(async () =>
            await connection.ExecuteAsync(sql, cancellationToken: SqlClientTestHarness.Timeout(30)));

        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CK00001");
        error.Message.ShouldContain($"ON UPDATE {action} action execution", Case.Sensitive);
        error.ConnectionUsable.ShouldBeTrue();
        var row = (await connection.QueryAsync("SELECT id,pid FROM t", cancellationToken: SqlClientTestHarness.Timeout(30))).ShouldHaveSingleItem();
        row[0].ShouldBe(1); row[1].ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - ON UPDATE: all metadata and default rules cross InMemory and TCP (#1723)")]
    [InlineData(false, "CASCADE")]
    [InlineData(false, "SET NULL")]
    [InlineData(false, "SET DEFAULT")]
    [InlineData(false, "RESTRICT")]
    [InlineData(false, "NO ACTION")]
    [InlineData(false, "")]
    [InlineData(true, "CASCADE")]
    [InlineData(true, "SET NULL")]
    [InlineData(true, "SET DEFAULT")]
    [InlineData(true, "RESTRICT")]
    [InlineData(true, "NO ACTION")]
    [InlineData(true, "")]
    public async Task QueryAsync_UpdateRule_ShouldReportStoredAction(bool tcp, string action)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout(30));
        await connection.ExecuteAsync("CREATE TABLE p(id INT PRIMARY KEY)", cancellationToken: SqlClientTestHarness.Timeout(30));
        await connection.ExecuteAsync("CREATE TABLE c(pid INT)", cancellationToken: SqlClientTestHarness.Timeout(30));
        string clause = action.Length == 0 ? "" : "ON UPDATE " + action;
        await connection.ExecuteAsync($"ALTER TABLE c ADD CONSTRAINT fk FOREIGN KEY(pid) REFERENCES p(id) ON DELETE CASCADE {clause}", cancellationToken: SqlClientTestHarness.Timeout(30));
        await connection.ExecuteAsync("CREATE VIEW rules AS SELECT UPDATE_RULE FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS", cancellationToken: SqlClientTestHarness.Timeout(30));

        // Act
        var rows = await connection.QueryAsync("SELECT UPDATE_RULE FROM rules", cancellationToken: SqlClientTestHarness.Timeout(30));

        // Assert
        rows.ShouldHaveSingleItem()[0].ShouldBe(action.Length == 0 ? "NO ACTION" : action);
    }
}
