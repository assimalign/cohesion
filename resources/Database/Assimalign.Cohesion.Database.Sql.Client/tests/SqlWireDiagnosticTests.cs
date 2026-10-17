using System;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

public sealed class SqlWireDiagnosticTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - parse and planning diagnostics survive the wire and the session is reused")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_DiagnosticFailures_ShouldRetainEveryFieldAndSession(bool tcp)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        const string sql = "SELECT NULLIF(1, 2), CURRENT_DATE FROM users;";
        var parsed = new SqlQueryParser().Parse(sql).Diagnostics.Where(item => item.Severity is DiagnosticSeverity.Warning or DiagnosticSeverity.Error).ToArray();
        // Act
        var parse = await Should.ThrowAsync<SqlClientException>(async () => await connection.QueryAsync(sql, cancellationToken: SqlClientTestHarness.Timeout()));
        var planning = await Should.ThrowAsync<SqlClientException>(async () => await connection.QueryAsync("SELECT absent FROM users", cancellationToken: SqlClientTestHarness.Timeout()));
        // Assert
        parsed.Length.ShouldBe(2);
        parse.Diagnostics.Select(Value).ShouldBe(parsed.Select(Value));
        parse.InnerException.ShouldBeOfType<DatabaseClientException>().Diagnostics.Select(Value).ShouldBe(parsed.Select(Value));
        planning.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHSQLE015");
        planning.Diagnostics[0].Start.ShouldBe(7);
        (await connection.QueryAsync("SELECT id FROM users", cancellationToken: SqlClientTestHarness.Timeout())).Count.ShouldBe(2);
        harness.Server.Sessions.Count.ShouldBe(1);
    }

    private static object Value(Diagnostic item) => (item.Code, item.Message, item.Severity, item.Start, item.End, item.Line);
}
