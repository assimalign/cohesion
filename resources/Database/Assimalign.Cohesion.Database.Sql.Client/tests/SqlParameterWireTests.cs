using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Protocol;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

public sealed class SqlParameterWireTests
{
    [Fact(DisplayName = "Cohesion Test [Sql.Client] - Parameters: sigils bind through the in-memory wire and missing values keep the connection")]
    public async Task QueryAsync_ParameterContract_ShouldBindAndKeepConnectionAsync()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        var command = new SqlCommand("SELECT name FROM users WHERE id = $id");
        command.Parameters.Add("@id", 2);

        // Act / Assert
        (await connection.QueryAsync(command, SqlClientTestHarness.Timeout())).ShouldHaveSingleItem().GetString("name").ShouldBe("grace");
        await connection.ExecuteAsync("CREATE TABLE empty_parameters (id INT)", cancellationToken: SqlClientTestHarness.Timeout());
        var failure = await Should.ThrowAsync<SqlClientException>(async () => await connection.QueryAsync(
            "SELECT id FROM empty_parameters WHERE id = $missing", cancellationToken: SqlClientTestHarness.Timeout()));
        failure.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
        failure.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        failure.Message.ShouldStartWith("CS01020:", Case.Sensitive);
        failure.ConnectionUsable.ShouldBeTrue();
        (await connection.QueryAsync("SELECT id FROM users WHERE id = 1", cancellationToken: SqlClientTestHarness.Timeout()))
            .ShouldHaveSingleItem().GetInt32("id").ShouldBe(1);
    }
}
