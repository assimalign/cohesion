using System.Linq;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Protocol;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

/// <summary>
/// A function call with arguments its signature does not accept, through the real SQL server and
/// the typed client (#1189): it is a coded execution failure, raised while planning, on a
/// connection that stays usable, and nothing changes. Before the fix such a call returned NULL, a
/// CHECK built on one was stored and never fired, and a write over an empty table succeeded.
/// </summary>
public sealed class SqlFunctionArityWireTests
{
    private const string FunctionSignatureMismatch = "COHSQLE006";

    /// <summary>Each position fails the same way, over the seeded table and an empty one.</summary>
    /// <param name="statement">A statement with a wrong-arity call.</param>
    /// <param name="message">The complete failure message.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - Functions: a wrong-arity call is a coded failure on a usable connection")]
    [InlineData("SELECT ABS(1, 2), UPPER(), COALESCE() FROM empty_rows",
        "COHSQLE006: Function 'ABS' takes exactly 1 argument but was called with 2. Accepted: ABS(numeric).")]
    [InlineData("SELECT id FROM users WHERE UPPER() IS NULL",
        "COHSQLE006: Function 'UPPER' takes exactly 1 argument but was called with none. Accepted: UPPER(value).")]
    [InlineData("UPDATE users SET score = COALESCE()",
        "COHSQLE006: Function 'COALESCE' takes 1 or more arguments but was called with none. Accepted: COALESCE(value [, value ...]).")]
    [InlineData("UPDATE empty_rows SET id = SUM()",
        "COHSQLE006: Function 'SUM' takes exactly 1 argument but was called with none. Accepted: SUM(numeric).")]
    [InlineData("INSERT INTO users (id, name, score) VALUES (3, LOWER('A', 'B'), 1)",
        "COHSQLE006: Function 'LOWER' takes exactly 1 argument but was called with 2. Accepted: LOWER(value).")]
    [InlineData("SELECT COUNT(id, name) FROM users",
        "COHSQLE006: Function 'COUNT' takes exactly 1 argument or '*' but was called with 2. Accepted: COUNT(*) or COUNT(value).")]
    [InlineData("CREATE TABLE ck1 (c INT CHECK (ABS(c, 1) > 0))",
        "COHSQLE006: Function 'ABS' takes exactly 1 argument but was called with 2. Accepted: ABS(numeric).")]
    [InlineData("ALTER TABLE users ADD CONSTRAINT ck CHECK (LENGTH(name, 1) > 0)",
        "COHSQLE006: Function 'LENGTH' takes exactly 1 argument but was called with 2. Accepted: LENGTH(value).")]
    public async Task ExecuteAsync_WrongArity_ShouldFailWithCodeAndKeepTheConnection(string statement, string message)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("CREATE TABLE empty_rows (id INT)", cancellationToken: SqlClientTestHarness.Timeout());

        // Act
        SqlClientException failure = await Should.ThrowAsync<SqlClientException>(async () =>
            await connection.ExecuteAsync(statement, cancellationToken: SqlClientTestHarness.Timeout()));

        // Assert
        failure.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
        failure.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        failure.ConnectionUsable.ShouldBeTrue();
        failure.Message.ShouldBe(message);
        failure.Message.ShouldStartWith(FunctionSignatureMismatch, Case.Sensitive);
        SqlResultSet users = await connection.QueryAsync("SELECT id, name, score FROM users ORDER BY id",
            cancellationToken: SqlClientTestHarness.Timeout());
        users.Select(row => (row["id"], row["name"], row["score"])).ShouldBe([(1, "ada", 100L), (2, "grace", 200L)]);
        SqlResultSet tables = await connection.QueryAsync(
            "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES ORDER BY TABLE_NAME", cancellationToken: SqlClientTestHarness.Timeout());
        tables.Select(row => row["TABLE_NAME"]).ShouldBe(new object?[] { "empty_rows", "users" });
        SqlResultSet checks = await connection.QueryAsync(
            "SELECT CONSTRAINT_NAME FROM INFORMATION_SCHEMA.CHECK_CONSTRAINTS", cancellationToken: SqlClientTestHarness.Timeout());
        checks.ShouldBeEmpty();
    }

    /// <summary>Calls within their signatures run on the same connection, COALESCE with one operand included.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - Functions: calls within their signatures run over the wire after a failure")]
    public async Task QueryAsync_CallsWithinTheirSignatures_ShouldRunAfterAFailure()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await Should.ThrowAsync<SqlClientException>(async () =>
            await connection.QueryAsync("SELECT ABS(1, 2) FROM users", cancellationToken: SqlClientTestHarness.Timeout()));

        // Act
        SqlResultSet rows = await connection.QueryAsync(
            "SELECT COALESCE(name) AS one, COALESCE(NULL, name) AS two, UPPER(name) AS up, LENGTH(name) AS len, ABS(-score) AS magnitude " +
            "FROM users ORDER BY id",
            cancellationToken: SqlClientTestHarness.Timeout());

        // Assert
        rows.Select(row => (row["one"], row["two"], row["up"], row["len"], row["magnitude"])).ShouldBe(
            [("ada", "ada", "ADA", 3L, 100L), ("grace", "grace", "GRACE", 5L, 200L)]);
    }
}
