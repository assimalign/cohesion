using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Types;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Client.Tests;

public sealed class GraphLinearCompositionWireTests
{
    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Composition: values, filters, FOR and WITH complete over both transports")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_LinearStatements_ShouldRoundTripRowsAndTypesAsync(bool tcp)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        var parameters = new Dictionary<string, object?> { ["xs"] = new object?[] { 1L, 2L }, ["value"] = 1L };
        // Act / Assert
        var scalar = await connection.QueryAsync("LET n = $value FILTER n > 0 RETURN n NEXT LET y = n + 1 RETURN y", parameters, harness.Token);
        scalar.ShouldHaveSingleItem().ToArray().ShouldBe(new object?[] { 2L });
        foreach (string source in new[] { "FOR x IN $xs RETURN x", "FOR x IN [1,2] RETURN x", "UNWIND [1,2] AS x WITH ALL x RETURN x" })
        { (await connection.QueryAsync(source, parameters, harness.Token)).Select(row => row[0]).ShouldBe(new object?[] { 1L, 2L }); }
        (await connection.ExecuteAsync("FOR x IN $xs INSERT (:P {k:x})", parameters, harness.Token)).ShouldBe(2);
        var count = await connection.QueryAsync("MATCH (a:P) WITH count(a) AS c RETURN c", cancellationToken: harness.Token);
        count.Columns.ShouldHaveSingleItem().Type.ShouldBe(DatabaseType.Int64);
        count.ShouldHaveSingleItem()[0].ShouldBe(2L);
        (await connection.QueryAsync("MATCH (a:P) WITH a ORDER BY a.k DESC LIMIT 1 WHERE a.k > 1 RETURN a.k", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(2L);
        (await connection.QueryAsync("INSERT (:Fresh {k:1}) NEXT MATCH (a:Fresh) RETURN a.k", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(1L);
        (await connection.QueryAsync("INSERT (:FreshTwo {k:2}) MATCH (a:FreshTwo) RETURN a.k", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(2L);
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Composition: coded rejections preserve the exact wire session")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_InvalidComposition_ShouldPreserveSessionAsync(bool tcp)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.ExecuteAsync("INSERT (:P {k:1})", cancellationToken: harness.Token);
        var sessionId = harness.Server.Sessions.ShouldHaveSingleItem().Id;
        var cases = new (string Source, string Code)[] {
            ("LET x=1 WITH DISTINCT x AS y WHERE x=1 RETURN y", "COHDBG001"),
            ("MATCH (a:P) WITH DISTINCT a.k AS k WHERE a.k > 1 RETURN k", "COHDBG001"),
            ("MATCH (a:P) WITH DISTINCT a AS b WHERE a.k > 1 RETURN b", "COHDBG001"),
            ("MATCH (a:P) WITH a.k AS k, count(*) AS c WHERE a.k > 1 RETURN k", "COHDBG001"),
            ("MATCH (a:P) WITH a AS b, count(*) AS c WHERE a.k > 1 RETURN b", "COHDBG001"),
            ("MATCH (a:P) WITH a.k AS k, count(*) AS c WHERE count(*) > 1 RETURN k", "COHDBG001"),
            ("INSERT (a:T {k:a.k})", "COHDBG001"),
            ("INSERT (a:T)-[r:R {w:1}]->(b:T {w:r.w})", "COHDBG001"),
            ("INSERT (a:T)-[r:R {w:r.w}]->(b:T)", "COHDBG001"),
            ("CREATE (a:T {k:a.k})", "COHDBG001"),
            ("CREATE (a:T)-[r:R {w:1}]->(b:T {w:r.w})", "COHDBG001"),
            ("CREATE (a:T)-[r:R {w:r.w}]->(b:T)", "COHDBG001"),
            ("LET x=1 WITH x,(x) RETURN x", "COHDBG015"),
            ("MATCH (a) RETURN a,\"a\" NEXT RETURN a", "COHDBG015"),
            ("LET x=1 RETURN x.k", "COHDBG003"),
            ("FOR m IN [{k:1},2] RETURN m.k", "COHDBG013"),
            ("MATCH (a) WITH a NEXT RETURN a", "GQL0002"),
            ("NEXT RETURN 1", "GQL0002"),
            ("EXPLAIN NEXT RETURN 1", "GQL0002"),
        };
        // Act / Assert
        foreach (var item in cases)
        {
            foreach (string prefix in item.Code is "COHDBG001" or "COHDBG003" or "COHDBG015" ? new[] { "", "EXPLAIN " } : new[] { "" })
            {
                var error = await Should.ThrowAsync<GraphClientException>(async () => await connection.QueryAsync(prefix + item.Source, cancellationToken: harness.Token));
                error.Message.ShouldContain(item.Code, Case.Sensitive);
                error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(item.Code);
                (await connection.QueryAsync("RETURN 7", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(7L);
                harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(sessionId);
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Composition: bound pattern maps, map members and quoted stars execute over both transports")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_BoundValues_ShouldRoundTripWithoutSessionLossAsync(bool tcp)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.ExecuteAsync("INSERT (:P {k:1,name:'Alice'})-[:T]->(:P {k:1,name:'Bob'}), (:P {k:2,name:'Cara'})-[:T]->(:P {k:3,name:'Dan'})", cancellationToken: harness.Token);
        // Act / Assert
        foreach (string source in new[] { "LET m={k:1} RETURN m.k", "FOR m IN [{k:1}] RETURN m.k", "LET \"x y\"=1 WITH * RETURN \"x y\"", "LET \"x y\"=1 RETURN * NEXT RETURN \"x y\"" })
        { (await connection.QueryAsync(source, cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(1L); }
        (await connection.QueryAsync("MATCH (n {k:n.k}) RETURN n.name ORDER BY n.name", cancellationToken: harness.Token)).Select(row => row[0]).ShouldBe(new object?[] { "Alice", "Bob", "Cara", "Dan" });
        (await connection.QueryAsync("MATCH (b:P) WITH b MATCH (a:P)-[:T]->(b {k:a.k}) RETURN a.name", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe("Alice");
        foreach (string boundary in new[] { "WITH *", "RETURN * NEXT" })
        {
            (await connection.QueryAsync("MATCH (\"my node\")->(b) " + boundary + " MATCH (\"my node\")->(c) RETURN c.name ORDER BY c.name", cancellationToken: harness.Token))
                .Select(row => row[0]).ShouldBe(new object?[] { "Bob", "Dan" });
        }
        (await connection.QueryAsync("MATCH (a:P) WITH collect(a) AS ns UNWIND ns AS n RETURN n.name ORDER BY n.name", cancellationToken: harness.Token)).Select(row => row[0]).ShouldBe(new object?[] { "Alice", "Bob", "Cara", "Dan" });
        await connection.ExecuteAsync("FOR row IN $rows INSERT (:Imported {name:row.name})", new Dictionary<string, object?> {
            ["rows"] = new object?[] { new Dictionary<string, object?> { ["name"] = "Eve" } } }, harness.Token);
        (await connection.QueryAsync("MATCH (n:Imported) RETURN n.name", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe("Eve");
        (await connection.QueryAsync("RETURN 7", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(7L);
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Composition: carried entities retain ExecutePaths gating and optionality")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryPathsAsync_Composition_ShouldPreserveEntitiesAndRejectWritesAsync(bool tcp)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.ExecuteAsync("INSERT (a:P {name:'Alice'})-[:T]->(:P {name:'Bob'})", cancellationToken: harness.Token);
        // Act / Assert
        foreach (string source in new[] { "MATCH (a:P) WITH a RETURN a", "MATCH (a:P) RETURN a NEXT RETURN a", "MATCH p=(a)-[:T]->(b) WITH p AS q RETURN q" })
        {
            var names = new List<object?>();
            await foreach (var path in connection.QueryPathsAsync(source, cancellationToken: harness.Token)) { names.Add(path.Nodes[0].Properties["name"]); }
            names.ShouldNotBeEmpty();
            var error = await Should.ThrowAsync<GraphClientException>(async () => await connection.QueryAsync(source, cancellationToken: harness.Token));
            error.Message.ShouldContain("ExecutePaths", Case.Sensitive);
        }
        foreach (string source in new[] { "INSERT (a:P) WITH a RETURN a", "OPTIONAL MATCH (a:P) WITH a RETURN a", "OPTIONAL MATCH p=(a)-[:T]->(b) RETURN p NEXT RETURN p" })
        {
            var error = await Should.ThrowAsync<GraphClientException>(async () =>
            { await foreach (var path in connection.QueryPathsAsync(source, cancellationToken: harness.Token)) { } });
            error.Message.ShouldContain("COHDBG001", Case.Sensitive);
        }
        (await connection.QueryAsync("MATCH (a:P) RETURN count(a)", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(2L);
    }
}
