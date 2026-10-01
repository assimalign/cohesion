using System;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

/// <summary>
/// Verifies the line-comment boundary (#1150) through <see cref="SqlDatabaseServer"/> and the
/// typed SQL client. A <c>--</c> comment used to end only at LF, so a WHERE after a lone CR was
/// comment text and the DELETE or UPDATE changed every row. At every line terminator the WHERE
/// now stays in effect over the wire and exactly one row changes.
/// </summary>
public sealed class SqlLineCommentWireTests
{
    /// <param name="terminator">The line terminator, by name (see <see cref="Terminator"/>).</param>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - Comments: DELETE with a WHERE after a line comment deletes exactly one row over the wire")]
    [InlineData("LF")]
    [InlineData("CR")]
    [InlineData("CRLF")]
    [InlineData("NEL")]
    [InlineData("LS")]
    [InlineData("PS")]
    public async Task ExecuteAsync_DeleteWithWhereAfterLineComment_ShouldDeleteExactlyOneRow(string terminator)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        string statement = "DELETE FROM users -- c" + Terminator(terminator) + "WHERE id = 1;";

        // Act
        long affected = await connection.ExecuteAsync(statement, cancellationToken: SqlClientTestHarness.Timeout());

        // Assert
        affected.ShouldBe(1);
        var users = await connection.QueryAsync("SELECT id, name, score FROM users ORDER BY id",
            cancellationToken: SqlClientTestHarness.Timeout());
        users.Select(row => (row["id"], row["name"], row["score"])).ShouldBe(
            new (object?, object?, object?)[] { (2, "grace", 200L) });
    }

    /// <param name="terminator">The line terminator, by name.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - Comments: UPDATE with a WHERE after a line comment updates exactly one row over the wire")]
    [InlineData("LF")]
    [InlineData("CR")]
    [InlineData("CRLF")]
    [InlineData("NEL")]
    [InlineData("LS")]
    [InlineData("PS")]
    public async Task ExecuteAsync_UpdateWithWhereAfterLineComment_ShouldUpdateExactlyOneRow(string terminator)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        string statement = "UPDATE users SET score = 1 -- c" + Terminator(terminator) + "WHERE id = 1;";

        // Act
        long affected = await connection.ExecuteAsync(statement, cancellationToken: SqlClientTestHarness.Timeout());

        // Assert
        affected.ShouldBe(1);
        var users = await connection.QueryAsync("SELECT id, name, score FROM users ORDER BY id",
            cancellationToken: SqlClientTestHarness.Timeout());
        users.Select(row => (row["id"], row["name"], row["score"])).ShouldBe(
            new (object?, object?, object?)[] { (1, "ada", 1L), (2, "grace", 200L) });
    }

    /// <summary>
    /// A statement on the line after a comment is leftover text: a parse failure that changes
    /// nothing and keeps the connection usable.
    /// </summary>
    /// <param name="terminator">The line terminator, by name.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - Comments: a statement after a line comment is a parse failure over the wire")]
    [InlineData("CR")]
    [InlineData("NEL")]
    [InlineData("LS")]
    [InlineData("PS")]
    public async Task ExecuteAsync_StatementAfterLineComment_ShouldReportParseFailureWithoutMutation(string terminator)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        string statement = "DELETE FROM users WHERE id = 1 -- c" + Terminator(terminator) + "DELETE FROM users";

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

    /// <summary>The line terminators by name, so no invisible character sits in the test source.</summary>
    /// <param name="name">LF, CR, CRLF, NEL, LS or PS.</param>
    /// <returns>The terminator text.</returns>
    private static string Terminator(string name) => name switch
    {
        "LF" => "\n",
        "CR" => "\r",
        "CRLF" => "\r\n",
        "NEL" => ((char)0x0085).ToString(),
        "LS" => ((char)0x2028).ToString(),
        "PS" => ((char)0x2029).ToString(),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown line terminator."),
    };
}
