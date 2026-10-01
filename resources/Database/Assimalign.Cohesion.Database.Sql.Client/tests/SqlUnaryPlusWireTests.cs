using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

/// <summary>
/// ISO unary plus through the real SQL server and the typed client: numeric values keep their
/// value and type, NULL propagates, and a non-numeric operand is a coded execution failure on a
/// connection that stays usable.
/// </summary>
public sealed class SqlUnaryPlusWireTests
{
    private const string InvalidOperandType = "COHSQLE003";

    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - Unary plus: values keep their type over the wire")]
    public async Task QueryAsync_UnaryPlus_ShouldReturnOperandUnchanged()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        var parameters = new Dictionary<string, object?> { ["p"] = 5 };

        // Act
        SqlResultSet result = await connection.QueryAsync(
            "SELECT +id AS id, +score AS score, +(score + 1) AS next, +@p AS bound, +NULL AS nothing FROM users WHERE +id = 1",
            parameters, SqlClientTestHarness.Timeout());

        // Assert
        result.Columns.Take(3).Select(column => column.Type).ShouldBe([DatabaseType.Int32, DatabaseType.Int64, DatabaseType.Int64]);
        var row = result.ShouldHaveSingleItem();
        new[] { row["id"], row["score"], row["next"], row["bound"], row["nothing"] }.ShouldBe(new object?[] { 1, 100L, 101L, 5, null });
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - Unary plus: a non-numeric operand is a coded execution failure")]
    [InlineData("SELECT +name FROM users")]
    [InlineData("SELECT +'abc' FROM users WHERE id = 0")]
    [InlineData("SELECT +TRUE FROM users")]
    [InlineData("SELECT -name FROM users")]
    [InlineData("UPDATE users SET score = +name")]
    public async Task QueryAsync_NonNumericOperand_ShouldFailWithCode(string statement)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());

        // Act
        SqlClientException failure = await Should.ThrowAsync<SqlClientException>(async () =>
            await connection.QueryAsync(statement, cancellationToken: SqlClientTestHarness.Timeout()));

        // Assert
        failure.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
        failure.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        failure.ConnectionUsable.ShouldBeTrue();
        failure.Message.ShouldStartWith(InvalidOperandType + ": Invalid operand type: unary", Case.Sensitive);
        SqlResultSet scores = await connection.QueryAsync("SELECT id, score FROM users ORDER BY id",
            cancellationToken: SqlClientTestHarness.Timeout());
        scores.Select(row => (row["id"], row["score"])).ShouldBe([(1, 100L), (2, 200L)]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - Unary plus: a non-numeric bound parameter fails when the row is evaluated")]
    public async Task QueryAsync_NonNumericParameter_ShouldFailWithCode()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        var parameters = new Dictionary<string, object?> { ["p"] = "abc" };

        // Act
        SqlClientException failure = await Should.ThrowAsync<SqlClientException>(async () =>
            await connection.QueryAsync("SELECT +@p FROM users", parameters, SqlClientTestHarness.Timeout()));

        // Assert
        failure.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
        failure.ConnectionUsable.ShouldBeTrue();
        failure.Message.ShouldBe(InvalidOperandType + ": Invalid operand type: unary '+' requires a numeric operand, but the operand is String.");
    }
}
