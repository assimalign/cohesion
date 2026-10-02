using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Protocol;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

/// <summary>
/// A column reference in a row of <c>INSERT ... VALUES</c> through the real SQL server and the typed
/// client (#1165): the statement is a coded execution failure on a connection that stays usable,
/// nothing is inserted, and literals and parameters keep inserting on the same connection. Before
/// the fix the reference escaped the engine as an <c>IndexOutOfRangeException</c>, which the server
/// reported as <c>Internal</c> and answered by closing the session.
/// </summary>
public sealed class SqlInsertValuesWireTests
{
    private const string ColumnReferenceNotAllowed = "COHSQLE005";

    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - INSERT VALUES: a column reference is a coded failure on a usable connection")]
    [InlineData("INSERT INTO users (id, name, score) VALUES (id, 'lin', 1)", "id")]
    [InlineData("INSERT INTO users (id, name, score) VALUES (3, 'lin', score + 1)", "score")]
    [InlineData("INSERT INTO users (id, name, score) VALUES (3, 'lin', 1), (4, users.name, 2)", "users.name")]
    public async Task ExecuteAsync_ColumnReferenceInValues_ShouldFailWithCode(string statement, string reference)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());

        // Act
        SqlClientException failure = await Should.ThrowAsync<SqlClientException>(async () =>
            await connection.ExecuteAsync(statement, cancellationToken: SqlClientTestHarness.Timeout()));

        // Assert
        failure.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
        failure.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        failure.ConnectionUsable.ShouldBeTrue();
        failure.Message.ShouldBe($"{ColumnReferenceNotAllowed}: Column reference '{reference}' is not allowed in INSERT ... VALUES, " +
            "which has no columns in scope. Use literals, parameters and expressions over them, or INSERT ... SELECT to read values from a table.");
        SqlResultSet ones = await connection.QueryAsync("SELECT 1 AS one FROM users", cancellationToken: SqlClientTestHarness.Timeout());
        ones.Select(row => row["one"]).ShouldBe(new object?[] { 1L, 1L });
        SqlResultSet users = await connection.QueryAsync("SELECT id, score FROM users ORDER BY id",
            cancellationToken: SqlClientTestHarness.Timeout());
        users.Select(row => (row["id"], row["score"])).ShouldBe([(1, 100L), (2, 200L)]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - INSERT VALUES: parameters and expressions over them still insert after a failure")]
    public async Task ExecuteAsync_ParametersAfterFailure_ShouldInsert()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        var parameters = new Dictionary<string, object?> { ["id"] = 3, ["name"] = "lin", ["score"] = 150L };
        await Should.ThrowAsync<SqlClientException>(async () =>
            await connection.ExecuteAsync("INSERT INTO users (id, name, score) VALUES (id, name, score)",
                cancellationToken: SqlClientTestHarness.Timeout()));

        // Act
        long affected = await connection.ExecuteAsync(
            "INSERT INTO users (id, name, score) VALUES (@id, UPPER(@name), @score * 2), (@id + 1, 'kay' || '!', -@score)",
            parameters, SqlClientTestHarness.Timeout());

        // Assert
        affected.ShouldBe(2);
        SqlResultSet users = await connection.QueryAsync("SELECT id, name, score FROM users WHERE id > 2 ORDER BY id",
            cancellationToken: SqlClientTestHarness.Timeout());
        users.Select(row => (row["id"], row["name"], row["score"])).ShouldBe([(3, "LIN", 300L), (4, "kay!", -150L)]);
    }
}
