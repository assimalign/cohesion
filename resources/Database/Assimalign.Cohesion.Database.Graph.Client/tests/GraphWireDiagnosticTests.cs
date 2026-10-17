using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Client.Tests;

public sealed class GraphWireDiagnosticTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Graph.Client] - full parse failures and engine codes survive negotiation")]
    public async Task QueryAsync_DiagnosticFailures_ShouldRetainEveryFieldAndSession()
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        const string gql = "MATCH ANY SHORTEST (a)->(b) RETURN a UNION MATCH (c) RETURN c";
        var expected = new GqlQueryParser().Parse(gql).Diagnostics.Where(item => item.Severity is DiagnosticSeverity.Warning or DiagnosticSeverity.Error).ToArray();
        // Act
        var parse = await Should.ThrowAsync<GraphClientException>(async () => await connection.QueryAsync(gql, cancellationToken: harness.Token));
        var plan = await Should.ThrowAsync<GraphClientException>(async () => await connection.QueryAsync("MATCH (n) RETURN n", cancellationToken: harness.Token));
        // Assert
        expected.Length.ShouldBeGreaterThanOrEqualTo(2);
        parse.Diagnostics.Select(Value).ShouldBe(expected.Select(Value));
        parse.InnerException.ShouldBeOfType<DatabaseClientException>().Diagnostics.Select(Value).ShouldBe(expected.Select(Value));
        plan.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBG001");
        await connection.QueryAsync("SHOW LABELS", cancellationToken: harness.Token);
        harness.Server.Sessions.Count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph.Client] - successful unknown-name reads carry warnings before scalar and path completion")]
    public async Task QueryAsync_UnknownLabels_ShouldReceiveWarningsAndCompletePaths()
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        // Act
        var result = await connection.QueryAsync("MATCH (n:Absent) RETURN n.name", cancellationToken: harness.Token);
        int paths = 0;
        await foreach (var path in connection.QueryPathsAsync("MATCH (n:Absent) RETURN n", cancellationToken: harness.Token)) { paths++; }
        // Assert
        result.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBG010");
        result.Diagnostics[0].Severity.ShouldBe(ProtocolDiagnosticSeverity.Warning);
        paths.ShouldBe(0);
        await connection.QueryAsync("SHOW LABELS", cancellationToken: harness.Token);
        harness.Server.Sessions.Count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph.Client] - path parse failures retain full diagnostics")]
    public async Task QueryPathsAsync_UnsupportedSyntax_ShouldReceiveDiagnostics()
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        // Act
        var error = await Should.ThrowAsync<GraphClientException>(async () =>
        {
            await foreach (var path in connection.QueryPathsAsync("MATCH ANY SHORTEST (a)->(b) RETURN a UNION MATCH (c) RETURN c", cancellationToken: harness.Token)) { }
        });
        // Assert
        error.Diagnostics.Count.ShouldBeGreaterThanOrEqualTo(2);
        error.Code.ShouldBe(ProtocolErrorCode.ParseFailure);
    }

    private static object Value(Diagnostic item) => (item.Code, item.Message, item.Severity, item.Start, item.End, item.Line);
}
