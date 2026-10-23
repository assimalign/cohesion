using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

public sealed class SqlPagingWireTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Client] - Paging: spellings return exact rows and metadata through SqlDatabaseServer")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_PagingForms_ShouldReturnRowsAndMetadata(bool tcp)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("CREATE TABLE t (id INT, name TEXT)", cancellationToken: SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("INSERT INTO t VALUES (40, 'd'), (10, 'a'), (50, 'e'), (20, 'b'), (30, 'c')", cancellationToken: SqlClientTestHarness.Timeout());
        string[] queries =
        [
            "SELECT id, name FROM t ORDER BY id OFFSET 1 ROWS FETCH NEXT 2 ROWS ONLY",
            "SELECT id, name FROM t ORDER BY id OFFSET 1 ROWS FETCH NEXT (1+1) ROWS ONLY",
            "SELECT id, name FROM t ORDER BY id OFFSET 1 LIMIT 2",
            "SELECT id, name FROM t ORDER BY id LIMIT 2 OFFSET 1",
            "SELECT TOP (2) id, name FROM t WHERE id > 10 ORDER BY id",
            "SELECT TOP 2 id, name FROM t WHERE id > 10 ORDER BY id",
        ];

        // Act / Assert
        foreach (string query in queries)
        {
            var result = await connection.QueryAsync(query, cancellationToken: SqlClientTestHarness.Timeout());
            result.Columns.Select(column => column.Name).ShouldBe(new[] { "id", "name" });
            result.Columns.Select(column => column.Type).ShouldBe(new[] { DatabaseType.Int32, DatabaseType.String });
            result.Select(row => row[0]).ShouldBe(new object?[] { 20, 30 });
            result.Select(row => row[1]).ShouldBe(new object?[] { "b", "c" });
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - Paging: rejections preserve coded diagnostics and a usable connection")]
    [InlineData("SELECT id FROM users LIMIT 1 FETCH FIRST 1 ROW ONLY", "CS00003")]
    [InlineData("SELECT TOP (1) id FROM users LIMIT 1", "CS00003")]
    [InlineData("SELECT TOP (1) id FROM users OFFSET 1 ROWS", "CS00003")]
    [InlineData("SELECT TOP (1) id FROM users FETCH FIRST 1 ROW ONLY", "CS00003")]
    [InlineData("SELECT id FROM users OFFSET 1 OFFSET 2", "CS00003")]
    [InlineData("SELECT id FROM users FETCH FIRST 1 ROW", "CS00003")]
    [InlineData("SELECT id FROM users FETCH FIRST 1+1 ROWS ONLY", "CS00003")]
    [InlineData("SELECT TOP id FROM users", "CS00003")]
    [InlineData("SELECT id FROM users FETCH FIRST 10 PERCENT ROWS ONLY", "CK00001")]
    [InlineData("SELECT id FROM users ORDER BY id FETCH FIRST 1 ROW WITH TIES", "CK00001")]
    [InlineData("SELECT TOP (10) PERCENT id FROM users", "CK00001")]
    [InlineData("SELECT TOP (1) WITH TIES * FROM users ORDER BY id", "CK00001")]
    [InlineData("DELETE TOP (1) FROM users", "CK00001")]
    [InlineData("UPDATE TOP (1) users SET id = 5", "CK00001")]
    [InlineData("INSERT TOP (1) INTO users SELECT id FROM users", "CK00001")]
    [InlineData("INSERT TOP (1) users SELECT id FROM users", "CK00001")]
    [InlineData("SELECT id FROM users FETCH FIRST (SELECT id FROM users) ROWS ONLY", "CK00001")]
    [InlineData("SELECT TOP (SELECT id FROM users) id FROM users", "CK00001")]
    [InlineData("SELECT TOP 1 id FROM users WINDOW w AS ()", "CK00001")]
    [InlineData("SELECT TOP (-1) id FROM users", "CS02018")]
    [InlineData("SELECT id FROM users FETCH FIRST -1 ROWS ONLY", "CS02018")]
    [InlineData("SELECT id FROM users OFFSET -1 ROWS", "CS02018")]
    public async Task QueryAsync_RejectedPaging_ShouldKeepConnectionUsable(string sql, string code)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());

        // Act
        var error = await Should.ThrowAsync<SqlClientException>(async () =>
            await connection.QueryAsync(sql, cancellationToken: SqlClientTestHarness.Timeout()));

        // Assert
        error.Code.ShouldBe(code == "CS02018" ? ProtocolErrorCode.ExecutionFailure : ProtocolErrorCode.ParseFailure);
        error.Message.ShouldContain(code, Case.Sensitive);
        if (sql.StartsWith("DELETE TOP") || sql.StartsWith("UPDATE TOP") || sql.StartsWith("INSERT TOP"))
        {
            error.Message.ShouldContain("DML TOP", Case.Sensitive);
        }
        error.ConnectionUsable.ShouldBeTrue();
        var rows = await connection.QueryAsync("SELECT id FROM users ORDER BY id", cancellationToken: SqlClientTestHarness.Timeout());
        rows.Select(row => row[0]).ShouldBe(new object?[] { 1, 2 });
    }
}
