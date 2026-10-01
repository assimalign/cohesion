using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

/// <summary>
/// Verifies statement completeness (#1068) through the SQL wire protocol: leftover text
/// is a ParseFailure that changes nothing, and an unknown function is an
/// ExecutionFailure even when no row exists to evaluate it on.
/// </summary>
public sealed class SqlStatementCompletenessWireTests
{
    /// <summary>Each statement used to execute a truncated prefix that would change the seeded users.</summary>
    /// <param name="statement">The statement whose tail the parser used to drop.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - Completeness: leftover text reports SQL0003 over the wire and changes nothing")]
    [InlineData("DELETE FROM users WHRE id = 1")]
    [InlineData("UPDATE users SET score = 0 WHRE id = 1")]
    [InlineData("UPDATE users SET score = :score WHERE id = 1")]
    [InlineData("DELETE FROM users WHERE score IS TRUE")]
    [InlineData("INSERT INTO users (id, name, score) VALUES (3, 'linus', 300) ON CONFLICT DO NOTHING")]
    [InlineData("DELETE FROM users WHERE id = 1; DELETE FROM users")]
    [InlineData("UPDATE users SET score = 0, name = 'x WHERE id = 1")]
    [InlineData("DELETE FROM users /* WHERE id = 1")]
    [InlineData("DELETE FROM users WHERE NOT (score NOT NULL)")]
    [InlineData("UPDATE users SET name = 'x', WHERE id = 1")]
    [InlineData("DELETE FROM users WHERE id IN (1, 3")]
    public async Task ExecuteAsync_LeftoverText_ShouldReportParseFailureWithoutMutation(string statement)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());

        // Act
        var error = await Should.ThrowAsync<SqlClientException>(async () =>
            await connection.ExecuteAsync(statement, cancellationToken: SqlClientTestHarness.Timeout()));

        // Assert
        error.Kind.ShouldBe(SqlClientErrorKind.ParseFailure);
        error.Message.ShouldContain("SQL0003", Case.Sensitive);
        error.ConnectionUsable.ShouldBeTrue();
        var users = await connection.QueryAsync("SELECT id, name, score FROM users ORDER BY id",
            cancellationToken: SqlClientTestHarness.Timeout());
        users.Select(row => (row["id"], row["name"], row["score"])).ShouldBe(
            new (object?, object?, object?)[] { (1, "ada", 100L), (2, "grace", 200L) });
    }

    /// <summary>
    /// A non-integer type argument used to reach the planner, whose raw FormatException
    /// the server reported as an internal error and ended the session. It is now a parse
    /// failure on a connection that stays usable.
    /// </summary>
    /// <param name="statement">DDL with a type argument that is not an unsigned integer literal.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - Completeness: a malformed type argument is a parse failure that keeps the session")]
    [InlineData("CREATE TABLE wide (a VARCHAR(MAX))")]
    [InlineData("ALTER TABLE users ADD COLUMN note VARCHAR(1e3)")]
    [InlineData("CREATE TABLE wide (a VARCHAR(25 5))")]
    public async Task ExecuteAsync_MalformedTypeArgument_ShouldReportParseFailureAndKeepTheSession(string statement)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());

        // Act
        var error = await Should.ThrowAsync<SqlClientException>(async () =>
            await connection.ExecuteAsync(statement, cancellationToken: SqlClientTestHarness.Timeout()));

        // Assert
        error.Kind.ShouldBe(SqlClientErrorKind.ParseFailure);
        error.Message.ShouldContain("SQL0003", Case.Sensitive);
        error.ConnectionUsable.ShouldBeTrue();
        (await connection.QueryAsync("SELECT COUNT(*) AS total FROM users", cancellationToken: SqlClientTestHarness.Timeout()))
            .ShouldHaveSingleItem()["total"].ShouldBe(2L);
    }

    /// <summary>The planner rejects an unknown function before any row is read, including over an empty table.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - Functions: an unknown function fails over the wire before any row is read")]
    public async Task QueryAsync_UnknownFunction_ShouldReportExecutionFailureOverTheWire()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("CREATE TABLE empty_rows (id INT)", cancellationToken: SqlClientTestHarness.Timeout());

        // Act
        var empty = await Should.ThrowAsync<SqlClientException>(async () => await connection.QueryAsync(
            "SELECT FOO(id) FROM empty_rows", cancellationToken: SqlClientTestHarness.Timeout()));
        var update = await Should.ThrowAsync<SqlClientException>(async () => await connection.ExecuteAsync(
            "UPDATE users SET score = FOO(score)", cancellationToken: SqlClientTestHarness.Timeout()));

        // Assert
        foreach (var error in new[] { empty, update })
        {
            error.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
            error.Message.ShouldContain("Unknown function 'FOO'.", Case.Sensitive);
            error.ConnectionUsable.ShouldBeTrue();
        }
        (await connection.QueryAsync("SELECT SUM(score) AS total FROM users", cancellationToken: SqlClientTestHarness.Timeout()))
            .ShouldHaveSingleItem()["total"].ShouldBe(300m);
    }
}
