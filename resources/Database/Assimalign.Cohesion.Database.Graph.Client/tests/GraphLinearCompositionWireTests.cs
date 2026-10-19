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

    [Theory(DisplayName = "Cohesion Test [Graph.Client] - MATCH: cross-part map predicates complete on the exact wire session")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_CrossPartProperties_ShouldEvaluateCompleteClauseAsync(bool tcp)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.ExecuteAsync("INSERT (:P {k:1})-[:T {w:1}]->(:P {k:1}), (:P {k:2})-[:T {w:2}]->(:P {k:3})", cancellationToken: harness.Token);
        var sessionId = harness.Server.Sessions.ShouldHaveSingleItem().Id;
        var cases = new (string Source, long Count)[] {
            ("MATCH (a:P {k:b.k}),(b:P)", 6),
            ("MATCH (b:P),(a:P {k:b.k})", 6),
            ("MATCH (a:P),(b:P) WHERE a.k=b.k", 6),
            ("MATCH (:P {k:b.k + 0}),(b:P)", 6),
            ("MATCH ()-[:T {w:b.k}]->(),(b:P)", 3),
            ("MATCH (b:P) WITH b MATCH (:P {k:c.k})->(b),(c:P)", 3),
            ("OPTIONAL MATCH (:P {k:b.k}),(b:P {k:99})", 1),
        };
        // Act / Assert
        foreach (var item in cases)
        {
            string source = item.Source + " RETURN count(*) AS c";
            (await connection.QueryAsync(source, cancellationToken: harness.Token)).ShouldHaveSingleItem().ToArray().ShouldBe(new object?[] { item.Count });
            (await connection.QueryAsync("EXPLAIN " + source, cancellationToken: harness.Token)).ShouldNotBeEmpty();
        }
        (await connection.QueryAsync("RETURN 7", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(7L);
        harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(sessionId);
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Client] - FOR and UNWIND: proven entity kinds survive wire MATCH and DELETE")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_EntityElements_ShouldCarryKindsAndPreserveSessionAsync(bool tcp)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.ExecuteAsync("INSERT (:P {name:'Alice'})-[:T {name:'AB'}]->(:P {name:'Bob'}), (:P {name:'Cara'})-[:T {name:'CD'}]->(:P {name:'Dan'})", cancellationToken: harness.Token);
        var sessionId = harness.Server.Sessions.ShouldHaveSingleItem().Id;
        // Act / Assert
        foreach (string loop in new[] { "FOR n IN ns", "UNWIND ns AS n" })
        {
            foreach (string boundary in new[] { "WITH n", "RETURN n NEXT" })
            {
                foreach (string list in new[] { "collect(a)", "[a]" })
                {
                    string source = "MATCH (a:P) WITH " + list + " AS ns " + loop + " " + boundary + " MATCH (n) RETURN n.name ORDER BY n.name";
                    (await connection.QueryAsync(source, cancellationToken: harness.Token)).Select(row => row[0]).ShouldBe(new object?[] { "Alice", "Bob", "Cara", "Dan" });
                    (await connection.QueryAsync("EXPLAIN " + source, cancellationToken: harness.Token)).ShouldNotBeEmpty();
                }
                string edges = "MATCH ()-[r:T]->() WITH collect(r) AS ns " + loop + " " + boundary + " MATCH ()-[n]->() RETURN n.name ORDER BY n.name";
                (await connection.QueryAsync(edges, cancellationToken: harness.Token)).Select(row => row[0]).ShouldBe(new object?[] { "AB", "CD" });
                (await connection.QueryAsync("EXPLAIN " + edges, cancellationToken: harness.Token)).ShouldNotBeEmpty();
            }
        }
        const string pathsSource = "MATCH (a:P) WITH collect(a) AS ns UNWIND ns AS n RETURN n NEXT MATCH (n) RETURN n";
        var names = new List<object?>();
        await foreach (var path in connection.QueryPathsAsync(pathsSource, cancellationToken: harness.Token))
        { names.Add(path.Nodes.ShouldHaveSingleItem().Properties["name"]); }
        names.OrderBy(name => name).ShouldBe(new object?[] { "Alice", "Bob", "Cara", "Dan" });
        var gate = await Should.ThrowAsync<GraphClientException>(async () => await connection.QueryAsync(pathsSource, cancellationToken: harness.Token));
        gate.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBG001");
        foreach (string source in new[] { "FOR n IN [1] WITH n DETACH DELETE n", "MATCH (a:P) WITH [a,1] AS ns UNWIND ns AS n MATCH (n) RETURN n.name" })
        {
            foreach (string prefix in new[] { "", "EXPLAIN " })
            {
                var error = await Should.ThrowAsync<GraphClientException>(async () => await connection.QueryAsync(prefix + source, cancellationToken: harness.Token));
                error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBG003");
                (await connection.QueryAsync("RETURN 7", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(7L);
            }
        }
        const string deleteEdges = "MATCH ()-[r:T]->() WITH collect(r) AS ns UNWIND ns AS n RETURN n NEXT DELETE n";
        const string deleteNodes = "MATCH (a:P) WITH collect(a) AS ns FOR n IN ns WITH n DETACH DELETE n";
        (await connection.QueryAsync("EXPLAIN " + deleteEdges, cancellationToken: harness.Token)).ShouldNotBeEmpty();
        (await connection.QueryAsync("EXPLAIN " + deleteNodes, cancellationToken: harness.Token)).ShouldNotBeEmpty();
        (await connection.ExecuteAsync(deleteEdges, cancellationToken: harness.Token)).ShouldBe(2);
        (await connection.ExecuteAsync(deleteNodes, cancellationToken: harness.Token)).ShouldBe(4);
        (await connection.QueryAsync("MATCH (a:P) RETURN count(*)", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(0L);
        harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(sessionId);
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
