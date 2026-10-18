using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

public sealed class SqlAlterTableWireTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - ALTER: renamed objects and changed defaults execute over the wire")]
    public async Task ExecuteAsync_AlterTable_ShouldRetainConnectionAndValues()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("CREATE TABLE t (id INT PRIMARY KEY, v INT)", cancellationToken: SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("INSERT INTO t VALUES (1, NULL)", cancellationToken: SqlClientTestHarness.Timeout());

        // Act
        await connection.ExecuteAsync("ALTER TABLE t RENAME TO renamed", cancellationToken: SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("ALTER TABLE renamed RENAME v TO value", cancellationToken: SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("ALTER TABLE renamed ALTER value SET DEFAULT -7", cancellationToken: SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("INSERT INTO renamed (id) VALUES (2)", cancellationToken: SqlClientTestHarness.Timeout());
        var error = await Should.ThrowAsync<SqlClientException>(async () => await connection.ExecuteAsync(
            "ALTER TABLE renamed ALTER value SET NOT NULL", cancellationToken: SqlClientTestHarness.Timeout()));

        // Assert
        error.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
        error.ConnectionUsable.ShouldBeTrue();
        var rows = await connection.QueryAsync("SELECT value FROM renamed ORDER BY id", cancellationToken: SqlClientTestHarness.Timeout());
        rows.Select(row => row[0]).ShouldBe(new object?[] { null, -7 });
        await connection.ExecuteAsync("UPDATE renamed SET value = 8 WHERE id = 1", cancellationToken: SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("ALTER TABLE renamed ALTER value SET NOT NULL", cancellationToken: SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("ALTER TABLE renamed ALTER value DROP NOT NULL", cancellationToken: SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("ALTER TABLE renamed ALTER value DROP DEFAULT", cancellationToken: SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("INSERT INTO renamed(id) VALUES (3)", cancellationToken: SqlClientTestHarness.Timeout());
        (await connection.QueryAsync("SELECT value FROM renamed ORDER BY id", cancellationToken: SqlClientTestHarness.Timeout()))
            .Select(row => row[0]).ShouldBe(new object?[] { 8, -7, null });
    }
}
