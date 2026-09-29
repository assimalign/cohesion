using System.Linq;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

/// <summary>Verifies COLLATE syntax, values and constraint failures through the actual SQL wire protocol.</summary>
public sealed class SqlCollationWireTests
{
    /// <summary>Columns and expression overrides reach predicates, grouping and typed results over the wire.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - Collation: column and expression overrides execute over the wire")]
    public async Task QueryAsync_CollatedColumnAndExpression_ShouldCompareGroupAndSortOverWire()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("CREATE TABLE collated (id INT, name TEXT COLLATE case_insensitive)",
            cancellationToken: SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("INSERT INTO collated VALUES (1, 'Alice'), (2, 'alice'), (3, 'Zed'), (4, 'bob')",
            cancellationToken: SqlClientTestHarness.Timeout());

        // Act
        var matched = await connection.QueryAsync(new SqlCommand("SELECT id FROM collated WHERE name = @name ORDER BY id")
            .WithParameter("name", "ALICE"), SqlClientTestHarness.Timeout());
        var exact = await connection.QueryAsync("SELECT id FROM collated WHERE name = 'alice' COLLATE binary",
            cancellationToken: SqlClientTestHarness.Timeout());
        var groups = await connection.QueryAsync("SELECT name, COUNT(*) AS total FROM collated GROUP BY name ORDER BY name",
            cancellationToken: SqlClientTestHarness.Timeout());
        var expression = await connection.QueryAsync("SELECT name COLLATE case_accent_insensitive AS label FROM collated WHERE name = 'ALICE' COLLATE case_insensitive AND id = 1",
            cancellationToken: SqlClientTestHarness.Timeout());

        // Assert
        matched.Select(row => row["id"]).ShouldBe(new object?[] { 1, 2 });
        exact.ShouldHaveSingleItem()["id"].ShouldBe(2);
        groups.Select(row => row["name"]).ShouldBe(new object?[] { "Alice", "bob", "Zed" });
        groups[0]["total"].ShouldBe(2L);
        expression.Columns.ShouldHaveSingleItem().Type.ShouldBe(DatabaseType.String);
        expression.ShouldHaveSingleItem()["label"].ShouldBe("Alice");
    }

    /// <summary>The unique backing index rejects normalized duplicates and leaves the connection usable.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - Collation: unique constraints reject folded duplicates over the wire")]
    public async Task ExecuteAsync_CollatedUniqueColumn_ShouldRejectFoldedDuplicateOverWire()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("CREATE TABLE collated_unique (name TEXT COLLATE case_accent_insensitive UNIQUE)",
            cancellationToken: SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("INSERT INTO collated_unique VALUES ('CAFÉ')", cancellationToken: SqlClientTestHarness.Timeout());

        // Act / Assert
        var error = await Should.ThrowAsync<SqlClientException>(async () => await connection.ExecuteAsync(
            "INSERT INTO collated_unique VALUES ('cafe')", cancellationToken: SqlClientTestHarness.Timeout()));
        error.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
        error.ConnectionUsable.ShouldBeTrue();
        (await connection.QueryAsync("SELECT name FROM collated_unique WHERE name LIKE 'ca_e'", cancellationToken: SqlClientTestHarness.Timeout()))
            .ShouldHaveSingleItem()["name"].ShouldBe("CAFÉ");
    }
}
