using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Client.Tests;

public sealed class GraphPathSelectorWireTests
{
    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Selector EXPLAIN succeeds and deferred execution preserves the same session (#1734)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_SelectorPlanning_ShouldExplainAndRefuseExecutionAsync(bool tcp)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        var sessionId = harness.Server.Sessions.ShouldHaveSingleItem().Id;

        // Act / Assert
        foreach (string selector in new[] { "ANY", "ALL", "ANY SHORTEST", "ALL SHORTEST", "SHORTEST 2", "SHORTEST 2 GROUPS" })
        {
            string pattern = $"MATCH p = {selector} ((a WHERE a.k>0)->{{0,3}}(b) WHERE path_length(p)>0)";
            var explain = await connection.QueryAsync("EXPLAIN " + pattern + " WHERE b.k=3 RETURN path_length(p)", cancellationToken: harness.Token);
            explain.ShouldContain(row => Equals(row[2], "SelectPaths"));
            explain.ShouldContain(row => Equals(row[2], "PathPrefilter"));
            explain.ShouldContain(row => Equals(row[2], "PostFilter"));
            explain.Single(row => Equals(row[2], "SelectPaths"))[5].ShouldBeOfType<string>().ShouldContain("partition=endpoint_pair", Case.Sensitive);

            var error = await Should.ThrowAsync<GraphClientException>(async () => await connection.QueryAsync(pattern + " RETURN a.k LIMIT 0", cancellationToken: harness.Token));
            error.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CK00001");
            var paths = await Should.ThrowAsync<GraphClientException>(async () =>
            {
                await foreach (var _ in connection.QueryPathsAsync(pattern + " RETURN p", cancellationToken: harness.Token)) { }
            });
            paths.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
            paths.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CK00001");
            (await connection.QueryAsync("RETURN 42", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(42L);
            harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(sessionId);
        }
        await connection.ExecuteAsync("BEGIN", cancellationToken: harness.Token);
        await connection.ExecuteAsync("INSERT (:Pending)", cancellationToken: harness.Token);
        var refusal = await Should.ThrowAsync<GraphClientException>(async () => await connection.QueryAsync(
            "MATCH ANY SHORTEST (a)->(b) RETURN a.k", cancellationToken: harness.Token));
        refusal.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CK00001");
        var aborted = await Should.ThrowAsync<GraphClientException>(async () => await connection.QueryAsync("RETURN 1", cancellationToken: harness.Token));
        aborted.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG04007");
        await connection.ExecuteAsync("ROLLBACK", cancellationToken: harness.Token);
        (await connection.QueryAsync("MATCH (n) RETURN count(*)", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(0L);
        harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(sessionId);
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Selector binding errors precede execution refusal on both transports (#1734)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_MissingPrefilterParameter_ShouldReportBindingDiagnosticAsync(bool tcp)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        // Act
        var error = await Should.ThrowAsync<GraphClientException>(async () => await connection.QueryAsync(
            "EXPLAIN MATCH ANY (a WHERE a.k=$missing) RETURN a.k", cancellationToken: harness.Token));
        // Assert
        error.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05001");
        (await connection.QueryAsync("RETURN 1", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(1L);
    }
}
