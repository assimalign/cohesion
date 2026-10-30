using System;
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
        const string gql = "MATCH (a)->(b) CALL foo() RETURN a HAVING TRUE";
        var expected = new GqlQueryParser().Parse(gql).Diagnostics.Where(item => item.Severity is DiagnosticSeverity.Warning or DiagnosticSeverity.Error).ToArray();
        // Act
        var parse = await Should.ThrowAsync<GraphClientException>(async () => await connection.QueryAsync(gql, cancellationToken: harness.Token));
        var plan = await Should.ThrowAsync<GraphClientException>(async () => await connection.QueryAsync("MATCH (n) RETURN n", cancellationToken: harness.Token));
        // Assert
        expected.Length.ShouldBeGreaterThanOrEqualTo(2);
        parse.Diagnostics.Select(Value).ShouldBe(expected.Select(Value));
        parse.InnerException.ShouldBeOfType<DatabaseClientException>().Diagnostics.Select(Value).ShouldBe(expected.Select(Value));
        plan.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05001");
        await connection.QueryAsync("SHOW LABELS", cancellationToken: harness.Token);
        harness.Server.Sessions.Count.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Graph.Client] - successful unknown-name reads carry warnings before scalar and path completion")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_UnknownLabels_ShouldReceiveWarningsAndCompletePaths(bool tcp)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        // Act
        var result = await connection.QueryAsync("MATCH (n:Absent) RETURN n.name", cancellationToken: harness.Token);
        int paths = 0;
        var pathResult = connection.QueryPathsAsync("MATCH (n:Absent) RETURN n", cancellationToken: harness.Token);
        await foreach (var path in pathResult) { paths++; }
        // Assert
        result.Diagnostics.Select(item => item.Code).ShouldBe(["CG01023", "CG01010"], ignoreOrder: true);
        result.Diagnostics[0].Severity.ShouldBe(ProtocolDiagnosticSeverity.Warning);
        paths.ShouldBe(0);
        Should.Throw<InvalidOperationException>(() => pathResult.GetAsyncEnumerator(harness.Token));
        pathResult.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG01010");
        pathResult.Diagnostics[0].Severity.ShouldBe(ProtocolDiagnosticSeverity.Warning);
        await connection.QueryAsync("SHOW LABELS", cancellationToken: harness.Token);
        harness.Server.Sessions.Count.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Graph.Client] - unknown entity properties use structured diagnostics over InMemory and TCP (#1249)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_UnknownProperties_ShouldRetainRowsDiagnosticsAndSessionAsync(bool tcp)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.QueryAsync("INSERT (:Keep {name:'k'})-[:LINK {edgeKey:1}]->(:Other {otherKey:2})", cancellationToken: harness.Token);
        const string source = "MATCH (n:Keep) WHERE n.never_seen IS NULL RETURN n.never_seen AS x ORDER BY n.never_seen";

        // Act
        var result = await connection.QueryAsync(source, cancellationToken: harness.Token);
        var paths = connection.QueryPathsAsync("MATCH (n:Keep {never_seen:1}) RETURN n", cancellationToken: harness.Token);
        int pathCount = 0;
        await foreach (var path in paths) { pathCount++; }
        var known = await connection.QueryAsync("MATCH (n:Keep) RETURN n.otherKey,n.edgeKey", cancellationToken: harness.Token);
        var map = await connection.QueryAsync("RETURN {name:1}.never_seen", cancellationToken: harness.Token);
        var write = await connection.QueryAsync("MATCH (n:Keep) SET n.introduced=1 RETURN n.never_seen", cancellationToken: harness.Token);

        // Assert
        result.ShouldHaveSingleItem()[0].ShouldBeNull();
        var warning = result.Diagnostics.ShouldHaveSingleItem();
        warning.Code.ShouldBe("CG01023");
        warning.Severity.ShouldBe(ProtocolDiagnosticSeverity.Warning);
        string message = warning.Message.ShouldNotBeNull();
        message.ShouldContain("'never_seen'", Case.Sensitive);
        message.ShouldNotContain("CG01023", Case.Sensitive);
        source[warning.Start!.Value..warning.End!.Value].ShouldBe("n.never_seen");
        pathCount.ShouldBe(0);
        paths.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG01023");
        known.Diagnostics.ShouldBeEmpty();
        map.Diagnostics.ShouldBeEmpty();
        write.Diagnostics.ShouldBeEmpty();
        await connection.QueryAsync("SHOW LABELS", cancellationToken: harness.Token);
        harness.Server.Sessions.Count.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Graph.Client] - path parse failures retain full diagnostics and the same session")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryPathsAsync_UnsupportedSyntax_ShouldReceiveDiagnostics(bool tcp)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        // Act
        var error = await Should.ThrowAsync<GraphClientException>(async () =>
        {
            await foreach (var path in connection.QueryPathsAsync("MATCH (a)->(b) CALL foo() RETURN a HAVING TRUE", cancellationToken: harness.Token)) { }
        });
        // Assert
        error.Diagnostics.Count.ShouldBeGreaterThanOrEqualTo(2);
        error.Code.ShouldBe(ProtocolErrorCode.ParseFailure);
        error.Diagnostics.Select(Value).ShouldBe(new GqlQueryParser().Parse("MATCH (a)->(b) CALL foo() RETURN a HAVING TRUE").Diagnostics.Select(Value));
        connection.IsOpen.ShouldBeTrue();
        var session = harness.Server.Sessions.Single();
        await connection.QueryAsync("SHOW LABELS", cancellationToken: harness.Token);
        harness.Server.Sessions.ShouldHaveSingleItem().ShouldBeSameAs(session);
    }

    private static object Value(Diagnostic item) => (item.Code, item.Message, item.Severity, item.Start, item.End, item.Line);
}
