using System;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Client.Tests;

public sealed class GraphPatternComprehensionWireTests
{
    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Pattern collections: scalar lists round-trip and entity lists retain capability admission")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_PatternCollection_ShouldPreserveValuesAndSessionAsync(bool tcp)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.ExecuteAsync("INSERT (a:N {k:1})-[:T {w:2}]->(b:N {k:2}), (a)-[:T {w:3}]->(b), (b)-[:T {w:4}]->(:N {k:3})", cancellationToken: harness.Token);
        Guid session = harness.Server.Sessions.ShouldHaveSingleItem().Id;
        // Act / Assert
        foreach (string query in new[]
        {
            "MATCH (a:N {k:1}) RETURN [(a)->(b) | b.k]",
            "MATCH (a:N {k:1}) RETURN [x IN [a] | [(x)->(b) | b.k]][0]",
            "MATCH (a:N {k:1}) RETURN reduce(s=[],x IN [a] | [(x)->(b) | b.k])",
        })
        { (await connection.QueryAsync(query, cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBeOfType<object?[]>().ShouldBe(new object?[] { 2L,2L }); }
        (await connection.QueryAsync("RETURN size([()-[rs:T*1..2]->() | [r IN rs | r.w]])", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(5L);
        var row = (await connection.QueryAsync("RETURN [p=(:N {k:1})-[r:T]->(b) WHERE r.w>2 | {k:b.k,n:size(nodes(p))}]", cancellationToken: harness.Token)).ShouldHaveSingleItem();
        row[0].ShouldBeOfType<object?[]>().Length.ShouldBe(1);
        (await connection.QueryAsync("OPTIONAL MATCH (a:Missing) RETURN [(a)->(b) | b.k]", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBeOfType<object?[]>().ShouldBeEmpty();
        (await connection.QueryAsync("MATCH (a:N) RETURN sum(size([(a)->(b) | b.k]))", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(3L);
        foreach (string expression in new[] { "[()-[r:T]->(b) | b]", "[()-[r:T]->() | r]", "[p=()-[:T]->() | p]", "[()-[:T]->(b) WHERE FALSE | b]", "[p=()-[:T]->() | {p:p}]", "reduce(s=[],x IN [1,2] | [()-[:T]->(b) | {s:s,b:b}])" })
        {
            var error = await Should.ThrowAsync<GraphClientException>(async () => await connection.QueryAsync("INSERT (:Unwritten) RETURN " + expression, cancellationToken: harness.Token));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05001");
            harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(session);
        }
        (await connection.QueryAsync("MATCH (n:Unwritten) RETURN count(*)", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(0L);
        foreach (var (statement, code) in new[]
        {
            ("RETURN [(a)->(b) | count(*)]", "CG05028"),
            ("LET a=1 RETURN [(a)->(b) | b.k]", "CG01019"),
            ("RETURN [(a)->(b) WHERE b:A | (B|C)]", "CG00014"),
        })
        {
            var error = await Should.ThrowAsync<GraphClientException>(async () => await connection.QueryAsync(statement, cancellationToken: harness.Token));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
            harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(session);
        }
        var explain = await connection.QueryAsync("EXPLAIN MATCH (a) RETURN [(a)->(b) | b.k]", cancellationToken: harness.Token);
        explain.SelectMany(row => row.ToArray()).ShouldContain(value => Equals(value, "CollectApply"));
        await connection.DisposeAsync();
        await using var again = await harness.Client.ConnectAsync(harness.Token);
        (await again.QueryAsync("RETURN size([(:N {k:1})->(b) | b.k])", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(2L);
        harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(session);
    }
}
