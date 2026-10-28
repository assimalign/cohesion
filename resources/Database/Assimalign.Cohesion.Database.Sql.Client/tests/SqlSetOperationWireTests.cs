using System.Linq;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

public sealed class SqlSetOperationWireTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Client] - Set operations: rows names and unified types cross SqlDatabaseServer")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_SetOperations_ShouldReturnCombinedRows(bool tcp)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());

        // Act / Assert
        var union = await connection.QueryAsync(
            "SELECT id AS key_id FROM users UNION ALL SELECT CAST(id AS BIGINT) FROM users ORDER BY 1 LIMIT 2 OFFSET 1",
            cancellationToken: SqlClientTestHarness.Timeout());
        union.Columns.Single().Name.ShouldBe("key_id");
        union.Columns.Single().Type.ShouldBe(DatabaseType.Int64);
        union.Select(row => row[0]).ShouldBe(new object?[] { 1L, 2L });
        var intersect = await connection.QueryAsync(
            "SELECT id FROM users INTERSECT SELECT id FROM users WHERE id = 2 ORDER BY id LIMIT 1",
            cancellationToken: SqlClientTestHarness.Timeout());
        intersect.Columns.Single().Name.ShouldBe("id");
        intersect.Columns.Single().Type.ShouldBe(DatabaseType.Int32);
        intersect.Select(row => row[0]).ShouldBe(new object?[] { 2 });
        var except = await connection.QueryAsync(
            "(SELECT id FROM users UNION ALL SELECT id FROM users) EXCEPT ALL SELECT id FROM users ORDER BY id OFFSET 1 ROW FETCH NEXT ROW ONLY",
            cancellationToken: SqlClientTestHarness.Timeout());
        except.Select(row => row[0]).ShouldBe(new object?[] { 2 });
        var member = await connection.QueryAsync(
            "SELECT id FROM users WHERE id IN (SELECT id FROM users EXCEPT SELECT id FROM users WHERE id = 1)",
            cancellationToken: SqlClientTestHarness.Timeout());
        member.Columns.Single().Type.ShouldBe(DatabaseType.Int32);
        member.Select(row => row[0]).ShouldBe(new object?[] { 2 });
        var quantified = await connection.QueryAsync(
            "SELECT id FROM users WHERE id = ALL (SELECT id FROM users UNION SELECT id FROM users)",
            cancellationToken: SqlClientTestHarness.Timeout());
        quantified.Columns.Single().Type.ShouldBe(DatabaseType.Int32);
        quantified.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - Set operations: rejections keep coded diagnostics and the connection")]
    [InlineData("SELECT id FROM users UNION", "CS00003")]
    [InlineData("SELECT id FROM users ORDER BY id UNION SELECT id FROM users", "CS00003")]
    [InlineData("SELECT id FROM users LIMIT 1 UNION SELECT id FROM users", "CS00003")]
    [InlineData("SELECT id FROM users OFFSET 1 UNION SELECT id FROM users", "CS00003")]
    [InlineData("SELECT id FROM users UNION ALL CORRESPONDING SELECT id FROM users", "CK00001")]
    [InlineData("SELECT id FROM users UNION SELECT id FROM users WINDOW w AS (ROWS CURRENT ROW)", "CK00001")]
    [InlineData("SELECT id FROM users WHERE id = ALL (SELECT id, name FROM users UNION SELECT id, name FROM users)", "CS05013")]
    [InlineData("SELECT id FROM users UNION SELECT id, name FROM users", "CS05013")]
    [InlineData("SELECT id FROM users UNION SELECT name FROM users", "CS02003")]
    [InlineData("INSERT INTO users (id) SELECT id FROM users UNION SELECT id, name FROM users", "CS05013")]
    [InlineData("INSERT INTO users (id) SELECT id FROM users UNION SELECT name FROM users", "CS02003")]
    [InlineData("SELECT id FROM users UNION SELECT id FROM users ORDER BY id + 1", "CS02018")]
    [InlineData("SELECT id FROM users UNION SELECT id FROM users ORDER BY users.id", "CS02018")]
    public async Task QueryAsync_InvalidSetOperation_ShouldKeepConnectionUsable(string sql, string code)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());

        // Act
        var error = await Should.ThrowAsync<SqlClientException>(async () =>
            await connection.QueryAsync(sql, cancellationToken: SqlClientTestHarness.Timeout()));

        // Assert
        error.Message.ShouldContain(code, Case.Sensitive);
        error.ConnectionUsable.ShouldBeTrue();
        if (code == "CS02003") { error.Message.ShouldContain("column 1", Case.Sensitive); }
        (await connection.QueryAsync("SELECT id FROM users ORDER BY id", cancellationToken: SqlClientTestHarness.Timeout()))
            .Select(row => row[0]).ShouldBe(new object?[] { 1, 2 });
    }
}
