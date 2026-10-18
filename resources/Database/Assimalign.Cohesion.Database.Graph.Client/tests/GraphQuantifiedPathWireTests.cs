using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Client.Tests;

public sealed class GraphQuantifiedPathWireTests
{
    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Quantified paths: row and path frames preserve trails over both transports")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_QuantifiedPaths_ShouldRoundTripRowsAndPathsAsync(bool tcp)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.ExecuteAsync("INSERT (a:V {name:'A'})-[:T {w:1}]->(b:V {name:'B'})-[:T {w:2}]->(c:V {name:'C'})-[:T {w:3}]->(:V {name:'D'})", cancellationToken: harness.Token);
        var sessionId = harness.Server.Sessions.ShouldHaveSingleItem().Id;
        // Act / Assert
        foreach (string segment in new[] { "-[:T]->{1,3}", "-[:T*1..3]->", " ((x)-[:T]->(y)){1,3} " })
        {
            var rows = await connection.QueryAsync("MATCH (a:V {name:'A'})" + segment + "(b) RETURN a.name,b.name", cancellationToken: harness.Token);
            rows.Select(row => row[0]).ShouldBe(new object?[] { "A", "A", "A" });
            rows.Select(row => row[1]).ShouldBe(new object?[] { "B", "C", "D" });
            var paths = new List<GraphPath>();
            await foreach (var path in connection.QueryPathsAsync("MATCH p = (a:V {name:'A'})" + segment + "(b) RETURN p", cancellationToken: harness.Token))
            { paths.Add(path); }
            paths.Select(path => path.Relationships.Count).ShouldBe(new[] { 1, 2, 3 });
            foreach (var path in paths)
            {
                path.Nodes.Count.ShouldBe(path.Relationships.Count + 1);
                for (int i = 0; i < path.Relationships.Count; i++)
                { path.Relationships[i].From.ShouldBe(path.Nodes[i].Id); path.Relationships[i].To.ShouldBe(path.Nodes[i + 1].Id); }
            }
        }
        await using (var session = await harness.Database.CreateSessionAsync(harness.Token))
        { await GraphSchema.Open(harness.Database, session).CreateIndexAsync("V", "by_name", "name", harness.Token); }
        var reverse = new List<GraphPath>();
        await foreach (var path in connection.QueryPathsAsync("MATCH p = (a)-[:T]->{1,3}(b:V {name:'D'}) RETURN p", cancellationToken: harness.Token))
        { reverse.Add(path); }
        reverse.Select(path => path.Relationships.Count).ShouldBe(new[] { 1, 2, 3 });
        reverse[^1].Nodes.Select(node => node.Properties["name"]).ShouldBe(new object?[] { "A", "B", "C", "D" });
        foreach (string loop in new[] { "FOR e IN r", "UNWIND r AS e" })
        {
            (await connection.QueryAsync("MATCH (a:V {name:'A'})-[r:T]->{2}(b) WITH r " + loop + " WITH e MATCH ()-[e]->() RETURN e.w", cancellationToken: harness.Token))
                .Select(row => row[0]).ShouldBe(new object?[] { 1L, 2L });
        }
        var malformed = await Should.ThrowAsync<GraphClientException>(async () => await connection.QueryAsync("MATCH (a)->{3,1}(b) RETURN a.name", cancellationToken: harness.Token));
        malformed.Code.ShouldBe(ProtocolErrorCode.ParseFailure);
        malformed.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("GQL0002");
        (await connection.QueryAsync("RETURN 42", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(42L);
        harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(sessionId);
    }
}
