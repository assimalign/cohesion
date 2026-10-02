using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Sql.Language;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

/// <summary>
/// Proves the expression nesting limit (#1151) through the real SQL server and the typed client.
/// A 200,000-term statement used to overflow the server's stack and end its process, taking every
/// session with it. Only genuine nesting counts now: a 10,000-term AND or OR predicate executes,
/// text nested deeper than the engine's configured limit (256 by default) is a ParseFailure, a
/// statement within the limit that the server's thread is too small for, or a LIKE match deeper
/// than the stack, is a COHSQLE004 ExecutionFailure, and the connection that sent any of them
/// keeps working.
/// </summary>
public sealed class SqlExpressionDepthWireTests
{
    private const int Limit = SqlQueryParserOptions.DefaultExpressionNestingLimit;
    private const int Hostile = 200_000;

    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - Nesting: an expression at the 256-level limit executes over the wire")]
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
    /// A chain of AND or OR terms is one level however long, so a 10,000-term predicate parses,
    /// plans and executes over the wire, in a query and in an update.
    /// </summary>
    /// <param name="keyword">The chain's operator.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - Nesting: a 10,000-term AND or OR predicate executes over the wire")]
    [InlineData("AND")]
    [InlineData("OR")]
    public async Task QueryAsync_TenThousandTermPredicate_ShouldExecute(string keyword)
    {
        // Arrange: either chain selects only the user with id 2.
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        string predicate = keyword == "AND"
            ? string.Join(" AND ", Enumerable.Range(3, 9_999).Select(id => $"id <> {Number(id)}")) + " AND id > 1"
            : string.Join(" OR ", Enumerable.Range(3, 9_999).Select(id => $"id = {Number(id)}")) + " OR id = 2";

        // Act
        var rows = await connection.QueryAsync($"SELECT name FROM users WHERE {predicate}", cancellationToken: SqlClientTestHarness.Timeout(30));
        var updated = await connection.ExecuteAsync($"UPDATE users SET score = 7 WHERE {predicate}", cancellationToken: SqlClientTestHarness.Timeout(30));

        // Assert
        rows.ShouldHaveSingleItem()["name"].ShouldBe("grace");
        updated.ShouldBe(1);
        (await connection.QueryAsync("SELECT score FROM users WHERE id = 2", cancellationToken: SqlClientTestHarness.Timeout()))
            .ShouldHaveSingleItem()["score"].ShouldBe(7L);
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

    /// <summary>The server parses statement text with its engine's configured limit.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - Nesting: the server applies its engine's configured limit")]
    public async Task ExecuteAsync_ConfiguredLimit_ShouldBeTheServersLimit()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync(configureEngine: options => options.ExpressionNestingLimit = 32);
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());

        // Act
        var atLimit = await connection.QueryAsync($"SELECT {Chain(32)} AS total FROM users WHERE id = 1", cancellationToken: SqlClientTestHarness.Timeout());
        var error = await Should.ThrowAsync<SqlClientException>(async () => await connection.QueryAsync(
            $"SELECT {Chain(33)} AS total FROM users WHERE id = 1", cancellationToken: SqlClientTestHarness.Timeout()));

        // Assert
        atLimit.ShouldHaveSingleItem()["total"].ShouldBe(32L);
        error.Kind.ShouldBe(SqlClientErrorKind.ParseFailure);
        error.Message.ShouldContain("SQL0006: Expression nesting exceeds the supported limit of 32 levels.", Case.Sensitive);
        error.ConnectionUsable.ShouldBeTrue();
    }

    /// <summary>
    /// A statement within a high configured limit runs where the server's thread has the stack for
    /// it, and where it does not, it is COHSQLE004, statement too complex (ISO SQLSTATE 54001), an
    /// ExecutionFailure: the text is valid, the thread ran out. 4,000 nested parentheses need a few
    /// MB to parse: more than the 1.5 MB a .NET thread has by default on Windows and macOS, less
    /// than the 8 MB it has on Linux. Either way the connection keeps working.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - Nesting: a statement within a high limit runs, or fails with COHSQLE004")]
    public async Task QueryAsync_WithinHighLimit_ShouldRunOrFailWithStatementTooComplex()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync(configureEngine: options =>
            options.ExpressionNestingLimit = SqlQueryParserOptions.MaximumExpressionNestingLimit);
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        string sql = $"SELECT {new string('(', 4_000)}1{new string(')', 4_000)} AS one FROM users WHERE id = 1";

        // Act
        SqlClientException? error = null;
        SqlResultSet? rows = null;
        try
        {
            rows = await connection.QueryAsync(sql, cancellationToken: SqlClientTestHarness.Timeout(30));
        }
        catch (SqlClientException exception)
        {
            error = exception;
        }

        // Assert
        if (error is null)
        {
            rows.ShouldNotBeNull().ShouldHaveSingleItem()["one"].ShouldBe(1L);
        }
        else
        {
            error.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
            error.Message.ShouldStartWith("COHSQLE004: Statement too complex", Case.Sensitive);
            error.ConnectionUsable.ShouldBeTrue();
        }
        (await connection.QueryAsync("SELECT COUNT(*) AS total FROM users", cancellationToken: SqlClientTestHarness.Timeout()))
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

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
