using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Graph.Protocol;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Client.Tests;

public sealed class GraphPathModeWireTests
{
    [Theory(DisplayName = "Cohesion Test [Graph.Client] - All path modes preserve rows and repeated identities over both transports (#1733)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_PathModes_ShouldPreserveWholePaths(bool tcp)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.ExecuteAsync("INSERT (a:V {k:0})-[:T]->(b:V:Pivot {k:1})-[:T]->(:V {k:2})-[:T]->(a)", cancellationToken: harness.Token);
        await using (var session = await harness.Database.CreateSessionAsync(harness.Token))
        { await GraphSchema.Open(harness.Database, session).CreateIndexAsync("Pivot", "by_k", "k", harness.Token); }
        var sessionId = harness.Server.Sessions.ShouldHaveSingleItem().Id;

        // Act / Assert
        foreach (var (mode, max) in new[] { ("WALK", 6), ("TRAIL", 3), ("SIMPLE", 3), ("ACYCLIC", 2), ("", 3) })
        {
            foreach (string segment in new[] { "-[:T]->{0,6}", " ((x)-[:T]->(y)){0,6} " })
            {
                string pattern = $"MATCH p = {mode} (a:V {{k:0}}){segment}(b)";
                var rows = await connection.QueryAsync(pattern + " RETURN path_length(p) AS n ORDER BY n", cancellationToken: harness.Token);
                rows.Select(row => row[0]).ShouldBe(Enumerable.Range(0, max + 1).Select(i => (object?)(long)i));
                var paths = new List<GraphPath>();
                await foreach (var path in connection.QueryPathsAsync(pattern + " RETURN p", cancellationToken: harness.Token))
                { paths.Add(path); }
                paths.Select(path => path.Relationships.Count).ShouldBe(Enumerable.Range(0, max + 1), ignoreOrder: true);
                var longest = paths.Single(path => path.Relationships.Count == max);
                longest.Nodes.Select(node => node.Properties["k"]).ShouldBe(Enumerable.Range(0, max + 1).Select(i => (object?)(long)(i % 3)));
                if (mode == "WALK")
                {
                    longest.Nodes[0].Id.ShouldBe(longest.Nodes[3].Id);
                    longest.Relationships[0].Id.ShouldBe(longest.Relationships[3].Id);
                }
            }
            var anchored = new List<GraphPath>();
            await foreach (var path in connection.QueryPathsAsync(
                $"MATCH p = {mode} (a)-[:T]->(b:Pivot {{k:1}}) ((x)-[:T]->(y)){{2}} (c) RETURN p", cancellationToken: harness.Token))
            { anchored.Add(path); }
            anchored.Count.ShouldBe(mode == "ACYCLIC" ? 0 : 1);
            if (anchored.Count != 0) { anchored[0].Nodes[0].Id.ShouldBe(anchored[0].Nodes[^1].Id); }
        }
        harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(sessionId);
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Deferred modes retain coded refusals, rollback and exact-session reuse (#1733)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_DeferredMode_ShouldRecoverOnSameSession(bool tcp)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        var sessionId = harness.Server.Sessions.ShouldHaveSingleItem().Id;

        // Act / Assert
        foreach (string source in new[]
        {
            "MATCH WALK (a)->+(b) RETURN a.k",
            "MATCH WALK ((a)->(b))* RETURN a.k",
            "MATCH (TRAIL (a)->(b)){1,2} RETURN a.k",
            "MATCH SIMPLE (ACYCLIC (a)->(b)) RETURN a.k",
            "MATCH ANY 2 WALK (a)->{1,3}(b) RETURN a.k",
        })
        {
            var error = await Should.ThrowAsync<GraphClientException>(async () => await connection.QueryAsync(source, cancellationToken: harness.Token));
            error.Code.ShouldBe(ProtocolErrorCode.ParseFailure);
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CK00001");
            await Should.ThrowAsync<GraphClientException>(async () =>
            {
                await foreach (var _ in connection.QueryPathsAsync(source, cancellationToken: harness.Token)) { }
            });
            (await connection.QueryAsync("RETURN 42", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(42L);
            harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(sessionId);
        }
        await connection.ExecuteAsync("BEGIN", cancellationToken: harness.Token);
        await connection.ExecuteAsync("INSERT (:Pending)", cancellationToken: harness.Token);
        await Should.ThrowAsync<GraphClientException>(async () => await connection.QueryAsync("MATCH WALK (a)->*(b) RETURN a.k", cancellationToken: harness.Token));
        var aborted = await Should.ThrowAsync<GraphClientException>(async () => await connection.QueryAsync("RETURN 1", cancellationToken: harness.Token));
        aborted.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG04007");
        await connection.ExecuteAsync("ROLLBACK", cancellationToken: harness.Token);
        (await connection.QueryAsync("MATCH (n) RETURN count(*)", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(0L);
        harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(sessionId);
    }
}
