using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Client.Tests;

public sealed class GraphReduceWireTests
{
    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Binding forms: NULL truth tables, ordered reduction and scope survive both transports")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_ReduceAndPredicates_ShouldPreserveSemanticsAsync(bool tcp)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        Guid session = harness.Server.Sessions.ShouldHaveSingleItem().Id;
        // Act / Assert
        foreach (var (source, expected) in new (string, object?[])[]
        {
            ("NULL",[null,null,null,null]), ("[]",[true,false,true,false]),
            ("[TRUE,TRUE]",[true,true,false,false]), ("[TRUE,FALSE]",[false,true,false,true]),
            ("[TRUE,NULL]",[null,true,false,null]), ("[FALSE,NULL]",[false,null,null,null]),
            ("[NULL,TRUE,TRUE]",[null,true,false,false]), ("[NULL]",[null,null,null,null]),
        })
        {
            (await connection.QueryAsync($"RETURN all(x IN {source} WHERE x),any(x IN {source} WHERE x),none(x IN {source} WHERE x),single(x IN {source} WHERE x)", cancellationToken: harness.Token)).ShouldHaveSingleItem().ToArray().ShouldBe(expected);
        }
        foreach (var (query, expected) in new (string, object?[])[]
        {
            ("RETURN reduce(a=0,x IN [1,2,3] | a*10+x)",[123L]),
            ("RETURN reduce(a=1/0,x IN NULL | a),reduce(a=42,x IN [] | 1/0),reduce(a=0,x IN [1,NULL] | a+x)",[null,42L,null]),
            ("RETURN reduce(a=0,x IN [1,2] | toString(x))",["2"]),
            ("LET a=99 RETURN reduce(a=0,x IN [1,2] | a+reduce(a=x,x IN [x] | a+x))+a",[105L]),
            ("FOR x IN [1,2,3] RETURN sum(reduce(a=0,x IN [x] | a+x)),all(x IN collect(x) WHERE x>0)",[6L,true]),
            ("RETURN [x IN [1,2] WHERE any(y IN [x] WHERE y>0) | reduce(a=0,y IN [x] | a+y)]",[new object?[] { 1L,2L }]),
        })
        { (await connection.QueryAsync(query, cancellationToken: harness.Token)).ShouldHaveSingleItem().ToArray().ShouldBe(expected); }
        var parameters = new Dictionary<string, object?> { ["xs"] = new object?[] { 1L, null, 3L } };
        (await connection.QueryAsync("RETURN reduce(a=0,x IN $xs | a+coalesce(x,0)),single(x IN $xs WHERE x IS NULL)", parameters, harness.Token)).ShouldHaveSingleItem().ToArray().ShouldBe(new object?[] { 4L, true });
        harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(session);
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Reduce shapes: scalar entity-group projections and mutations execute; entity results refuse before writes")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_EntityGroupsAndUpdates_ShouldRetainAdmissionAsync(bool tcp)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.ExecuteAsync("INSERT (:N {k:1})-[:T {w:2}]->(:N {k:2})-[:T {w:3}]->(:N {k:3})", cancellationToken: harness.Token);
        Guid session = harness.Server.Sessions.ShouldHaveSingleItem().Id;
        const string match = "MATCH (a:N {k:1})-[rs:T]->{2}(b) ";
        // Act / Assert
        (await connection.QueryAsync(match + "RETURN reduce(s=0,r IN rs | s+r.w),all(r IN rs WHERE r.w>0)", cancellationToken: harness.Token)).ShouldHaveSingleItem().ToArray().ShouldBe(new object?[] { 5L, true });
        (await connection.QueryAsync(match + "WITH reduce(n=a,r IN rs | endNode(r)) AS n RETURN n.k", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(3L);
        (await connection.QueryAsync(match + "RETURN (reduce(n=a,r IN rs | endNode(r))).k", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(3L);
        foreach (string value in new[] { "reduce(n=a,r IN rs | endNode(r))", "reduce(n=0,r IN rs | r)", "reduce(n=0,r IN [] | a)", "reduce(n=a,r IN [] | 0)", "reduce(n=[],r IN rs | [n,r])", "reduce(n={r:a},r IN rs | n)" })
        {
            var error = await Should.ThrowAsync<GraphClientException>(async () => await connection.QueryAsync(match + "INSERT (:Unwritten) RETURN " + value, cancellationToken: harness.Token));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05001");
        }
        foreach (var (value, code) in new[] { ("all(x IN [] WHERE 1)", "CG02013"), ("any(x IN NULL WHERE [])", "CG02013"), ("reduce(a=0,x IN 1 | a)", "CG02015"), ("reduce(a=0,x IN [] | sum(x))", "CG05025"), ("reduce(x=0,x IN [] | x)", "CG01018") })
        {
            foreach (string prefix in new[] { "INSERT (:Unwritten) RETURN ", "EXPLAIN RETURN " })
            {
                var error = await Should.ThrowAsync<GraphClientException>(async () => await connection.QueryAsync(prefix + value, cancellationToken: harness.Token));
                error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
            }
        }
        await connection.ExecuteAsync("FOR k IN [1,2,1] MERGE (n:M {k:reduce(a=0,x IN [k] | a+x)}) ON CREATE SET n.c=reduce(a=10,x IN [k] | a+x),n.p=none(x IN [k] WHERE x<0) ON MATCH SET n.m=reduce(a=20,x IN [k] | a+x)", cancellationToken: harness.Token);
        var rows = await connection.QueryAsync("MATCH (n:M) WHERE any(x IN [n.k] WHERE x>0) RETURN n.k,n.c,n.m,n.p ORDER BY n.k", cancellationToken: harness.Token);
        rows.Count.ShouldBe(2);
        rows[0].ToArray().ShouldBe(new object?[] { 1L, 11L, 21L, true });
        rows[1].ToArray().ShouldBe(new object?[] { 2L, 12L, null, true });
        (await connection.QueryAsync("MATCH (n:Unwritten) RETURN count(*)", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(0L);
        (await connection.QueryAsync("EXPLAIN RETURN reduce(a=0,x IN [1] | a+x),all(x IN [] WHERE TRUE)", cancellationToken: harness.Token)).ShouldNotBeEmpty();
        harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(session);
    }
}
