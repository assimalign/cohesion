using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

/// <summary>
/// Verifies parse strictness (#1101) through the SQL wire protocol: a character outside the
/// dialect and the <c>~</c> operator are parse failures that execute nothing and keep the
/// connection usable.
/// </summary>
public sealed class SqlParseStrictnessWireTests
{
    /// <param name="statement">A statement with a stray character or with ~.</param>
    /// <param name="code">The diagnostic code the failure carries.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - Strictness: stray characters and ~ are parse failures over the wire")]
    [InlineData("SELECT * FROM users ?", "SQL0003")]
    [InlineData("SELECT id FROM users WHERE score = ?", "SQL0003")]
    [InlineData("DELETE FROM users ?", "SQL0003")]
    [InlineData("UPDATE users SET score = 0 #", "SQL0003")]
    [InlineData("SELECT ~score FROM users", "COHDBL001")]
    [InlineData("SELECT ~NULL", "COHDBL001")]
    [InlineData("DELETE FROM users WHERE name ~ 'a'", "COHDBL001")]
    public async Task ExecuteAsync_StrictnessViolation_ShouldReportParseFailureWithoutMutation(string statement, string code)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());

        // Act
        var error = await Should.ThrowAsync<SqlClientException>(async () =>
            await connection.ExecuteAsync(statement, cancellationToken: SqlClientTestHarness.Timeout()));

        // Assert
        error.Kind.ShouldBe(SqlClientErrorKind.ParseFailure);
        error.Message.ShouldContain(code, Case.Sensitive);
        error.ConnectionUsable.ShouldBeTrue();
        var users = await connection.QueryAsync("SELECT id, name, score FROM users ORDER BY id",
            cancellationToken: SqlClientTestHarness.Timeout());
        users.Select(row => (row["id"], row["name"], row["score"])).ShouldBe(
            new (object?, object?, object?)[] { (1, "ada", 100L), (2, "grace", 200L) });
    }
}
