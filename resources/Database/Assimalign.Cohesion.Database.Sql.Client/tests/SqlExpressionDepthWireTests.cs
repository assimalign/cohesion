using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

/// <summary>
/// Proves the expression nesting limit (#1151) through the real SQL server and the typed client.
/// A 200,000-term statement used to overflow the server's stack and end its process, taking every
/// session with it. Text deeper than 128 levels is now a ParseFailure, a LIKE match deeper than
/// the stack an ExecutionFailure, and the connection that sent either keeps working.
/// </summary>
public sealed class SqlExpressionDepthWireTests
{
    private const int Limit = 128;
    private const int Hostile = 200_000;

    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - Nesting: an expression at the 128-level limit executes over the wire")]
    public async Task QueryAsync_AtLimit_ShouldExecute()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());

        // Act
        var rows = await connection.QueryAsync($"SELECT {Chain(Limit)} AS total FROM users WHERE id = 1",
            cancellationToken: SqlClientTestHarness.Timeout());

        // Assert
        rows.ShouldHaveSingleItem()["total"].ShouldBe((long)Limit);
    }

    /// <summary>
    /// One level past the limit, and the 200,000-term chain the #1068 review crashed the server
    /// with, are parse failures. The server stays up: the same connection, and a new one, serve.
    /// </summary>
    /// <param name="terms">The number of terms in a <c>1 + 1 + ...</c> chain.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - Nesting: an expression past the limit is a ParseFailure that keeps the connection")]
    [InlineData(Limit + 1)]
    [InlineData(Hostile)]
    public async Task ExecuteAsync_PastLimit_ShouldReportParseFailureAndKeepServing(int terms)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());

        // Act
        var error = await Should.ThrowAsync<SqlClientException>(async () => await connection.ExecuteAsync(
            $"UPDATE users SET score = {Chain(terms)} WHERE id = 1", cancellationToken: SqlClientTestHarness.Timeout(30)));

        // Assert
        error.Kind.ShouldBe(SqlClientErrorKind.ParseFailure);
        error.Message.ShouldContain("SQL0006", Case.Sensitive);
        error.ConnectionUsable.ShouldBeTrue();
        (await connection.QueryAsync("SELECT 1 AS one FROM users WHERE id = 1", cancellationToken: SqlClientTestHarness.Timeout()))
            .ShouldHaveSingleItem()["one"].ShouldBe(1L);
        (await connection.QueryAsync("SELECT score FROM users WHERE id = 1", cancellationToken: SqlClientTestHarness.Timeout()))
            .ShouldHaveSingleItem()["score"].ShouldBe(100L);

        await using var other = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        (await other.QueryAsync("SELECT COUNT(*) AS total FROM users", cancellationToken: SqlClientTestHarness.Timeout()))
            .ShouldHaveSingleItem()["total"].ShouldBe(2L);
    }

    /// <summary>LIKE matching that runs out of stack is a coded execution failure, not a crash.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - Nesting: a LIKE match deeper than the stack fails with COHSQLE004 and keeps the connection")]
    public async Task QueryAsync_DeepLikeMatch_ShouldFailWithStatementTooComplex()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        var parameters = new Dictionary<string, object?>
        {
            ["value"] = new string('a', Hostile),
            ["pattern"] = string.Concat(Enumerable.Repeat("%a", Hostile)),
        };

        // Act
        var error = await Should.ThrowAsync<SqlClientException>(async () => await connection.QueryAsync(
            "SELECT id FROM users WHERE @value LIKE @pattern", parameters, SqlClientTestHarness.Timeout(30)));

        // Assert
        error.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
        error.Message.ShouldStartWith("COHSQLE004:", Case.Sensitive);
        error.ConnectionUsable.ShouldBeTrue();
        (await connection.QueryAsync("SELECT COUNT(*) AS total FROM users", cancellationToken: SqlClientTestHarness.Timeout()))
            .ShouldHaveSingleItem()["total"].ShouldBe(2L);
    }

    private static string Chain(int terms) => string.Join(" + ", Enumerable.Repeat("1", terms));
}
