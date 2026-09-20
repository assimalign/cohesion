using System;
using System.Linq;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

/// <summary>
/// Exercises JOIN through encoded SQL exchanges between SqlDatabaseServer and
/// the production typed SQL client, including its error mapping and metadata.
/// </summary>
public sealed class SqlJoinWireTests
{
    /// <summary>The owner's exact schema-qualified query returns typed matching rows over the wire.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - JOIN: owner query executes over the wire")]
    public async Task QueryAsync_OwnerJoin_ShouldReturnMatchingPairsOverTheWire()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await SeedAsync(connection);

        // Act: preserve the motivating query, which deliberately has no ORDER BY.
        var result = await connection.QueryAsync(
            "SELECT usr.Users.FirstName, usr.Users.LastName, usr.UsersProfile.Email " +
            "FROM usr.Users INNER JOIN usr.UsersProfile ON usr.Users.Id = usr.UsersProfile.UserId",
            cancellationToken: SqlClientTestHarness.Timeout());

        // Assert: compare the complete set independently of physical iteration order.
        result.Columns.Select(column => column.Name).ShouldBe(["FirstName", "LastName", "Email"]);
        result.Columns.Select(column => column.Type).ShouldBe([DatabaseType.String, DatabaseType.String, DatabaseType.String]);
        result.Select(row => $"{row[0]}|{row[1]}|{row[2]}").OrderBy(value => value, StringComparer.Ordinal).ShouldBe(
            ["Ada|Lovelace|ada-alt@example.test", "Ada|Lovelace|ada@example.test", "Grace|Hopper|grace@example.test"]);
        result.SelectMany(row => new[] { row[0], row[1], row[2] }).ShouldAllBe(value => value is string);
    }

    /// <summary>Aliased joins retain filter, sort, and pagination semantics through the client.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - JOIN: filters ordering and pagination survive the wire")]
    public async Task QueryAsync_JoinClauses_ShouldComposeOverTheWire()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await SeedAsync(connection);
        var command = new SqlCommand(
            "SELECT u.FirstName, p.Email FROM usr.Users u JOIN usr.UsersProfile p ON u.Id = p.UserId " +
            "WHERE u.Id >= @minimum AND p.Email LIKE '%example.test' ORDER BY u.Id DESC, p.Email ASC LIMIT 1 OFFSET 1")
            .WithParameter("minimum", 1);

        // Act
        var result = await connection.QueryAsync(command, SqlClientTestHarness.Timeout());

        // Assert
        var row = result.ShouldHaveSingleItem();
        row["FirstName"].ShouldBe("Ada");
        row["Email"].ShouldBe("ada-alt@example.test");
    }

    /// <summary>A nonmatching or empty joined side returns a successful typed empty result.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - JOIN: no matches and empty inputs return no rows over the wire")]
    public async Task QueryAsync_NoMatchesAndEmptySide_ShouldReturnEmptyResultsOverTheWire()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await SeedAsync(connection);

        // Act / Assert
        var nonmatching = await connection.QueryAsync(
            "SELECT u.FirstName, p.Email FROM usr.Users u JOIN usr.UsersProfile p ON u.Id = p.UserId WHERE u.Id = 3",
            cancellationToken: SqlClientTestHarness.Timeout());
        nonmatching.ShouldBeEmpty();
        nonmatching.Columns.Select(column => column.Type).ShouldBe([DatabaseType.String, DatabaseType.String]);
        await connection.ExecuteAsync("DELETE FROM usr.UsersProfile", cancellationToken: SqlClientTestHarness.Timeout());
        var empty = await connection.QueryAsync(
            "SELECT u.FirstName, p.Email FROM usr.Users u JOIN usr.UsersProfile p ON u.Id = p.UserId",
            cancellationToken: SqlClientTestHarness.Timeout());
        empty.ShouldBeEmpty();
    }

    /// <summary>Unimplemented join forms produce the capability diagnostic before planning over the wire.</summary>
    /// <param name="statement">The excluded join form.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - JOIN: excluded forms report COHDBL001 over the wire")]
    [InlineData("SELECT u.id FROM users u LEFT JOIN users v ON u.id = v.id")]
    [InlineData("SELECT u.id FROM users u RIGHT OUTER JOIN users v ON u.id = v.id")]
    [InlineData("SELECT u.id FROM users u FULL OUTER JOIN users v ON u.id = v.id")]
    [InlineData("SELECT u.id FROM users u CROSS JOIN users v")]
    [InlineData("SELECT u.id FROM users u NATURAL JOIN users v")]
    [InlineData("SELECT u.id FROM users u JOIN users v USING (id)")]
    [InlineData("SELECT u.id FROM users u JOIN users v ON u.id = v.id JOIN users w ON v.id = w.id")]
    public async Task QueryAsync_ExcludedJoin_ShouldReportCapabilityDiagnosticOverTheWire(string statement)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());

        // Act / Assert
        var error = await Should.ThrowAsync<SqlClientException>(async () =>
            await connection.QueryAsync(statement, cancellationToken: SqlClientTestHarness.Timeout()));
        error.Kind.ShouldBe(SqlClientErrorKind.ParseFailure);
        error.Message.ShouldContain("COHDBL001", Case.Sensitive);
        (await connection.QueryAsync("SELECT u.id FROM users u JOIN users v ON u.id = v.id ORDER BY u.id",
            cancellationToken: SqlClientTestHarness.Timeout())).Select(row => row[0]).ShouldBe(new object?[] { 1, 2 });
    }

    /// <summary>Ambiguous binding errors remain explicit and leave the client connection usable.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - JOIN: ambiguous names report execution errors over the wire")]
    public async Task QueryAsync_AmbiguousJoinColumn_ShouldReportBindingErrorOverTheWire()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());

        // Act / Assert
        var error = await Should.ThrowAsync<SqlClientException>(async () => await connection.QueryAsync(
            "SELECT id FROM users u JOIN users v ON u.id = v.id", cancellationToken: SqlClientTestHarness.Timeout()));
        error.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
        error.Message.ShouldContain("ambiguous");
        (await connection.QueryAsync("SELECT u.id FROM users u JOIN users v ON u.id = v.id WHERE u.id = 1",
            cancellationToken: SqlClientTestHarness.Timeout())).ShouldHaveSingleItem()[0].ShouldBe(1);
    }

    private static async Task SeedAsync(ISqlConnection connection)
    {
        await connection.ExecuteAsync("CREATE TABLE usr.Users (Id INT PRIMARY KEY, FirstName TEXT, LastName TEXT)",
            cancellationToken: SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("CREATE TABLE usr.UsersProfile (Id INT PRIMARY KEY, UserId INT REFERENCES usr.Users(Id), Email TEXT)",
            cancellationToken: SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("CREATE INDEX ix_profile_user ON usr.UsersProfile(UserId)",
            cancellationToken: SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("INSERT INTO usr.Users VALUES (1, 'Ada', 'Lovelace'), (2, 'Grace', 'Hopper'), (3, 'Alan', 'Turing')",
            cancellationToken: SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("INSERT INTO usr.UsersProfile VALUES (10, 1, 'ada@example.test'), (11, 1, 'ada-alt@example.test'), (20, 2, 'grace@example.test'), (90, NULL, 'unlinked@example.test')",
            cancellationToken: SqlClientTestHarness.Timeout());
    }
}
