using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Graph.Protocol;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Client.Tests;

public sealed class GraphPathSelectorWireTests
{
    [Theory(DisplayName = "Cohesion Test [Graph.Client] - All selectors execute scalar rows and complete paths over InMemory and TCP (#1734)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_SelectorCorpus_ShouldExecuteAndPreserveSessionAsync(bool tcp)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        var sessionId = harness.Server.Sessions.ShouldHaveSingleItem().Id;
        await connection.ExecuteAsync("INSERT (a:Start {k:0})-[:T {w:1}]->(b {k:1})-[:T {w:1}]->(z:End {k:9}), " +
            "(a)-[:T {w:1}]->(c {k:2})-[:T {w:1}]->(z), (a)-[:T {w:1}]->(d {k:3})-[:T {w:1}]->(e {k:4})-[:T {w:1}]->(z), " +
            "(s:Loop)-[:L]->(s)", cancellationToken: harness.Token);
        // Act / Assert
        foreach (var item in new[] { ("ANY", 1), ("ALL", 3), ("ANY SHORTEST", 1), ("ALL SHORTEST", 2), ("SHORTEST 2", 2), ("SHORTEST 2 GROUPS", 3) })
        {
            string pattern = $"MATCH p = {item.Item1} ((a:Start WHERE a.k=0)-[r:T WHERE r.w>0]->{{0,4}}(b:End) WHERE path_length(p)>0)";
            var explain = await connection.QueryAsync("EXPLAIN " + pattern + " WHERE b.k=9 RETURN path_length(p)", cancellationToken: harness.Token);
            explain.ShouldContain(row => Equals(row[2], "SelectPaths"));
            explain.ShouldContain(row => Equals(row[2], "PathPrefilter"));
            explain.ShouldContain(row => Equals(row[2], "PostFilter"));
            explain.Single(row => Equals(row[2], "SelectPaths"))[5].ShouldBeOfType<string>().ShouldContain("partition=endpoint_pair", Case.Sensitive);
            (await connection.QueryAsync(pattern + " RETURN path_length(p)", cancellationToken: harness.Token)).Count.ShouldBe(item.Item2);
            var paths = new List<GraphPath>();
            await foreach (var path in connection.QueryPathsAsync(pattern + " RETURN p", cancellationToken: harness.Token)) { paths.Add(path); }
            paths.Count.ShouldBe(item.Item2);
            paths[0].Nodes[1].Properties["k"].ShouldBe(1L);
            (await connection.QueryAsync(pattern + " WHERE path_length(p)>3 RETURN a.k", cancellationToken: harness.Token)).ShouldBeEmpty();
            harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(sessionId);
        }
        foreach (string mode in new[] { "WALK", "TRAIL", "SIMPLE", "ACYCLIC" })
        {
            (await connection.QueryAsync($"MATCH p = ANY SHORTEST {mode} (s:Loop)->*(s) RETURN path_length(p)", cancellationToken: harness.Token))
                .ShouldHaveSingleItem()[0].ShouldBe(0L);
        }
        await connection.ExecuteAsync("BEGIN", cancellationToken: harness.Token);
        await connection.ExecuteAsync("INSERT (:Pending)", cancellationToken: harness.Token);
        (await connection.QueryAsync("MATCH p = ANY SHORTEST (:Start)-[:T]->*(:End) RETURN path_length(p)", cancellationToken: harness.Token))
            .ShouldHaveSingleItem()[0].ShouldBe(2L);
        var refusal = await Should.ThrowAsync<GraphClientException>(async () => await connection.QueryAsync(
            "MATCH p = ALL WALK (s:Loop)->*(s) RETURN path_length(p)", cancellationToken: harness.Token));
        refusal.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        refusal.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05004");
        var aborted = await Should.ThrowAsync<GraphClientException>(async () => await connection.QueryAsync("RETURN 1", cancellationToken: harness.Token));
        aborted.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG04007");
        await connection.ExecuteAsync("ROLLBACK", cancellationToken: harness.Token);
        (await connection.QueryAsync("MATCH (n:Pending) RETURN count(*)", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(0L);
        harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(sessionId);
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Selector work and memory budgets carry coded terminal failures over both transports (#1734)")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task QueryAsync_SelectorBudget_ShouldRefuseAndReuseSessionAsync(bool tcp, bool memory)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync(tcp, options: memory
            ? new() { QueryMemoryBudget = 18000 } : new() { QueryWorkBudget = 100 });
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.ExecuteAsync("INSERT (s:Loop)-[:L]->(s)", cancellationToken: harness.Token);
        var sessionId = harness.Server.Sessions.ShouldHaveSingleItem().Id;
        // Act
        var error = await Should.ThrowAsync<GraphClientException>(async () => await connection.QueryAsync(
            "MATCH p = ALL WALK (s:Loop)-[:L]->*(s) RETURN path_length(p)", cancellationToken: harness.Token));
        // Assert
        error.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(memory ? "CG05020" : "CG05004");
        (await connection.QueryAsync("RETURN 42", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(42L);
        harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(sessionId);
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Selector binding errors precede search on both transports (#1734)")]
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
