using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

/// <summary>Execution diagnostics retain their carriers through both transports (#1653).</summary>
public sealed class SqlExecutionDiagnosticWireTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Client] - Execution failures: both transports retain exact codes, spans and unprefixed detail (#1653)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_ExecutionFailures_ShouldKeepDiagnosticsAndSession(bool tcp)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("CREATE DOMAIN d AS INT", cancellationToken: SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("CREATE TABLE target (id INT, v d)", cancellationToken: SqlClientTestHarness.Timeout());
        (string Sql, string Code)[] cases =
        [
            ("SELECT (SELECT id FROM users)", "CS02046"),
            ("INSERT INTO users VALUES (3, 'x', 'bad')", "CS02022"),
            ("CREATE TABLE users (id INT)", "CS03049"),
            ("ALTER TABLE users DROP CONSTRAINT absent", "CS01052"),
            ("ALTER TABLE users ADD COLUMN id INT", "CS03061"),
            ("DROP DOMAIN d", "CS03302"),
        ];

        foreach (var item in cases)
        {
            // Act
            var failure = await Should.ThrowAsync<SqlClientException>(async () =>
                await connection.QueryAsync("\n  " + item.Sql, cancellationToken: SqlClientTestHarness.Timeout()));

            // Assert
            failure.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
            failure.ConnectionUsable.ShouldBeTrue();
            var diagnostic = failure.Diagnostics.ShouldHaveSingleItem();
            diagnostic.Code.ShouldBe(item.Code);
            diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
            diagnostic.Location.ShouldBe(DiagnosticLocation.Absolute);
            diagnostic.Start.ShouldBe(3);
            diagnostic.End.ShouldBe(3 + item.Sql.Length);
            failure.Message.ShouldBe(item.Code + ": " + diagnostic.Message);
            diagnostic.Message!.ShouldNotContain(item.Code + ":", Case.Sensitive);
            (await connection.QueryAsync("SELECT id FROM users", cancellationToken: SqlClientTestHarness.Timeout())).Count.ShouldBe(2);
        }
    }

}
