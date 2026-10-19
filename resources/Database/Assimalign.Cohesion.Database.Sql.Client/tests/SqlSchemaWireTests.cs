using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

public sealed class SqlSchemaWireTests
{
    [Fact(DisplayName = "Cohesion Test [Sql.Client] - SQL namespace creation, RESTRICT and CASCADE cross server connections")]
    public async Task ExecuteAsync_SchemaDdl_ShouldPublishAndCascadeOverWire()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var writer = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await using var reader = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await ExecuteAsync(writer, "CREATE SCHEMA sales");
        await ExecuteAsync(writer, "CREATE TABLE Sales.t(id INT PRIMARY KEY)");
        await ExecuteAsync(writer, "CREATE TABLE child(pid INT REFERENCES sales.t(id))");
        await ExecuteAsync(writer, "INSERT INTO sales.t VALUES (1)");
        await ExecuteAsync(writer, "INSERT INTO child VALUES (1)");

        // Act / Assert
        var failure = await Should.ThrowAsync<SqlClientException>(() => ExecuteAsync(writer, "DROP SCHEMA sales RESTRICT"));
        failure.Message.ShouldContain("COHSQLE030");
        failure.ConnectionUsable.ShouldBeTrue();
        (await reader.QueryAsync("SELECT TABLE_SCHEMA FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 't'", cancellationToken: SqlClientTestHarness.Timeout()))
            .ShouldHaveSingleItem()[0].ShouldBe("sales");
        await ExecuteAsync(writer, "DROP SCHEMA sales CASCADE");
        await ExecuteAsync(writer, "INSERT INTO child VALUES (99)");
        (await reader.QueryAsync("SELECT pid FROM child ORDER BY pid", cancellationToken: SqlClientTestHarness.Timeout()))
            .Select(row => row[0]).ShouldBe(new object?[] { 1, 99 });
        (await reader.QueryAsync("SELECT SCHEMA_NAME FROM INFORMATION_SCHEMA.SCHEMATA WHERE SCHEMA_NAME = 'sales'", cancellationToken: SqlClientTestHarness.Timeout())).ShouldBeEmpty();
        await ExecuteAsync(writer, "DROP SCHEMA IF EXISTS sales");
        await ExecuteAsync(writer, "CREATE SCHEMA sales");
        await ExecuteAsync(writer, "DROP SCHEMA sales");
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - Schema failures preserve codes and leave the wire connection usable")]
    [InlineData("CREATE TABLE missing.t(id INT)", "COHSQLE028")]
    [InlineData("CREATE SCHEMA s AUTHORIZATION owner", "COHDBL001")]
    [InlineData("CREATE SCHEMA s CREATE TABLE t(id INT)", "COHDBL001")]
    [InlineData("DROP SCHEMA dbo", "COHSQLE031")]
    [InlineData("CREATE SCHEMA INFORMATION_SCHEMA", "COHSQLE021")]
    public async Task ExecuteAsync_RejectedSchemaDdl_ShouldCarryDiagnostic(string sql, string code)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());

        // Act
        var failure = await Should.ThrowAsync<SqlClientException>(() => ExecuteAsync(connection, sql));

        // Assert
        failure.Message.ShouldContain(code);
        failure.ConnectionUsable.ShouldBeTrue();
        await ExecuteAsync(connection, "CREATE SCHEMA usable");
    }

    private static Task<long> ExecuteAsync(SqlConnection connection, string sql)
        => connection.ExecuteAsync(sql, cancellationToken: SqlClientTestHarness.Timeout()).AsTask();
}
