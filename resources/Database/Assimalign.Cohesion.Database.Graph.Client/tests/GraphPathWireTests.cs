using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Client.Tests;

/// <summary>Verifies real engine paths survive GraphDatabaseServer and the typed client's streaming lifetime.</summary>
public sealed class GraphPathWireTests
{
    /// <summary>Wire paths preserve original identities, multi-label nodes, typed relationships, and scalar property runtime types.</summary>
    [Fact(DisplayName = "Cohesion Test [Graph.Client] - Paths preserve graph identities labels types and properties")]
    public async Task QueryPathsAsync_EntityProjection_ShouldPreserveCompleteGraphValues()
    {
        // Arrange: seed through the public entity API so expected ids and every property runtime type are known.
        await using var harness = await GraphClientTestHarness.StartAsync();
        var properties = new Dictionary<string, object?>
        {
            ["name"] = "Alice", ["null"] = null, ["true"] = true, ["false"] = false,
            ["byte"] = byte.MaxValue, ["sbyte"] = sbyte.MinValue, ["short"] = short.MinValue,
            ["ushort"] = ushort.MaxValue, ["int"] = int.MinValue, ["uint"] = uint.MaxValue,
            ["long"] = long.MinValue, ["ulong"] = ulong.MaxValue, ["float"] = 1.25f,
            ["double"] = 1.5e100, ["decimal"] = 12.50m,
        };
        GraphNode alice;
        GraphNode bob;
        GraphRelationship edge;
        await using (var session = await harness.Database.CreateSessionAsync(harness.Token))
        {
            alice = await harness.Database.CreateNodeAsync(session, ["Person", "Employee"], properties, harness.Token);
            bob = await harness.Database.CreateNodeAsync(session, ["Person"],
                new Dictionary<string, object?> { ["name"] = "Bob" }, harness.Token);
            edge = await harness.Database.CreateRelationshipAsync(session, alice.Id, bob.Id, "KNOWS",
                new Dictionary<string, object?> { ["weight"] = (ushort)23, ["note"] = "colleague" }, harness.Token);
        }
        await using var connection = await harness.Client.ConnectAsync(harness.Token);

        // Act
        var nodes = await CollectAsync(connection.QueryPathsAsync("MATCH (a:Employee) RETURN a AS person", cancellationToken: harness.Token));
        var relationships = await CollectAsync(connection.QueryPathsAsync(
            "MATCH (:Person {name: 'Bob'})<-[r:KNOWS]-() RETURN r", cancellationToken: harness.Token));
        var paths = await CollectAsync(connection.QueryPathsAsync(
            "MATCH p = (a:Employee)-[:KNOWS]->(b) RETURN p", cancellationToken: harness.Token));

        // Assert
        var nodePath = nodes.ShouldHaveSingleItem();
        nodePath.Relationships.ShouldBeEmpty();
        nodePath.Nodes.ShouldHaveSingleItem().Id.ShouldBe(alice.Id);
        nodePath.Nodes[0].Labels.ShouldBe(alice.Labels);
        foreach (var property in properties)
        {
            nodePath.Nodes[0].Properties[property.Key].ShouldBe(property.Value);
            nodePath.Nodes[0].Properties[property.Key]?.GetType().ShouldBe(property.Value?.GetType());
        }
        var relationshipPath = relationships.ShouldHaveSingleItem();
        relationshipPath.Nodes.Select(node => node.Id).ShouldBe([alice.Id, bob.Id]);
        var actualEdge = relationshipPath.Relationships.ShouldHaveSingleItem();
        actualEdge.Id.ShouldBe(edge.Id);
        actualEdge.Type.ShouldBe("KNOWS");
        actualEdge.From.ShouldBe(alice.Id);
        actualEdge.To.ShouldBe(bob.Id);
        actualEdge.Properties["weight"].ShouldBeOfType<ushort>().ShouldBe((ushort)23);
        actualEdge.Properties["note"].ShouldBe("colleague");
        var namedPath = paths.ShouldHaveSingleItem();
        namedPath.Nodes.Select(node => node.Id).ShouldBe([alice.Id, bob.Id]);
        namedPath.Relationships.ShouldHaveSingleItem().Id.ShouldBe(edge.Id);
    }

    /// <summary>Indexed middle anchors and reverse traversals retain path order and stored edge direction.</summary>
    [Fact(DisplayName = "Cohesion Test [Graph.Client] - Named paths preserve order around indexed middle anchors")]
    public async Task QueryPathsAsync_IndexedReverseTraversal_ShouldPreserveTraversalAndDirection()
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.ExecuteAsync(
            "CREATE (:Vertex {name: 'A'})-[:LINK {weight: 2}]->(:Vertex {name: 'B'})-[:LINK {weight: 3}]->(:Vertex {name: 'C'})",
            cancellationToken: harness.Token);
        await using (var session = await harness.Database.CreateSessionAsync(harness.Token))
        {
            await GraphSchema.Open(harness.Database, session).CreateIndexAsync("Vertex", "by_name", "name", harness.Token);
        }

        // Act
        var paths = await CollectAsync(connection.QueryPathsAsync(
            "MATCH p = (a:Vertex)<-[:LINK]-(b:Vertex {name: 'B'})<-[:LINK]-(c:Vertex) RETURN p",
            cancellationToken: harness.Token));

        // Assert
        var path = paths.ShouldHaveSingleItem();
        path.Nodes.Select(node => node.Properties["name"]).ShouldBe(["C", "B", "A"]);
        path.Relationships.Select(edge => edge.Properties["weight"]).ShouldBe([3L, 2L]);
        path.Relationships[0].From.ShouldBe(path.Nodes[1].Id);
        path.Relationships[0].To.ShouldBe(path.Nodes[0].Id);
        path.Relationships[1].From.ShouldBe(path.Nodes[2].Id);
        path.Relationships[1].To.ShouldBe(path.Nodes[1].Id);
    }

    /// <summary>Finite cyclic paths may revisit a node but never reuse an edge for an additional hop.</summary>
    [Fact(DisplayName = "Cohesion Test [Graph.Client] - Cyclic paths preserve finite trail semantics")]
    public async Task QueryPathsAsync_Cycle_ShouldRepeatNodesWithoutRepeatingRelationships()
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.ExecuteAsync("CREATE (a:Vertex {name: 'A'})-[:LINK]->(b:Vertex {name: 'B'})-[:LINK]->(a)",
            cancellationToken: harness.Token);

        // Act
        var paths = await CollectAsync(connection.QueryPathsAsync(
            "MATCH p = (a:Vertex {name: 'A'})-[:LINK]->(b)-[:LINK]->(a) RETURN p", cancellationToken: harness.Token));
        var tooLong = await CollectAsync(connection.QueryPathsAsync(
            "MATCH p = (a:Vertex {name: 'A'})-[:LINK]->()-[:LINK]->()-[:LINK]->() RETURN p", cancellationToken: harness.Token));

        // Assert
        var path = paths.ShouldHaveSingleItem();
        path.Nodes.Select(node => node.Properties["name"]).ShouldBe(["A", "B", "A"]);
        path.Nodes[0].Id.ShouldBe(path.Nodes[2].Id);
        path.Relationships.Select(edge => edge.Id).Distinct().Count().ShouldBe(2);
        tooLong.ShouldBeEmpty();
    }

    /// <summary>Full and empty path enumerations consume completion before returning the session to the pool.</summary>
    [Fact(DisplayName = "Cohesion Test [Graph.Client] - Successful path streams permit exact session reuse")]
    public async Task QueryPathsAsync_CompleteAndEmpty_ShouldReuseExactSession()
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync();
        Guid sessionId;
        await using (var connection = await harness.Client.ConnectAsync(harness.Token))
        {
            sessionId = harness.Server.Context.Sessions.ShouldHaveSingleItem().Id;
            await connection.ExecuteAsync("CREATE (:Person {name: 'Alice'}), (:Person {name: 'Bob'})", cancellationToken: harness.Token);

            // Act / Assert
            (await CollectAsync(connection.QueryPathsAsync("MATCH (a:Person) RETURN a", cancellationToken: harness.Token))).Count.ShouldBe(2);
            (await CollectAsync(connection.QueryPathsAsync("MATCH (a:Person {name: 'Missing'}) RETURN a",
                cancellationToken: harness.Token))).ShouldBeEmpty();
            connection.IsOpen.ShouldBeTrue();
            (await connection.QueryAsync("MATCH (a:Person) RETURN a.name", cancellationToken: harness.Token)).Count.ShouldBe(2);
        }
        await using var reused = await harness.Client.ConnectAsync(harness.Token);
        (await reused.QueryAsync("SHOW LABELS", cancellationToken: harness.Token)).ShouldHaveSingleItem()[2].ShouldBe("Person");
        harness.Server.Context.Sessions.ShouldHaveSingleItem().Id.ShouldBe(sessionId);
    }

    /// <summary>Requests with scalar or mutation projections fail explicitly and cannot perform writes through ExecutePaths.</summary>
    /// <param name="statement">The unsupported path query shape.</param>
    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Invalid path requests fail explicitly and discard their streams")]
    [InlineData("MATCH (a) RETURN a.name")]
    [InlineData("MATCH (a) RETURN a, a.name")]
    [InlineData("CREATE (a:Injected) RETURN a")]
    [InlineData("MATCH (a) DELETE a")]
    public async Task QueryPathsAsync_InvalidShape_ShouldRejectAndDiscard(string statement)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        Guid oldId = harness.Server.Context.Sessions.ShouldHaveSingleItem().Id;

        // Act / Assert
        var error = await Should.ThrowAsync<GraphClientException>(async () =>
            await CollectAsync(connection.QueryPathsAsync(statement, cancellationToken: harness.Token)));
        error.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        connection.IsOpen.ShouldBeFalse();
        // Streaming failures conservatively discard even when the server was able to reject before output.
        await using var fresh = await harness.Client.ConnectAsync(harness.Token);
        (await fresh.QueryAsync("SHOW LABELS", cancellationToken: harness.Token)).ShouldBeEmpty();
        harness.Server.Context.Sessions.ShouldContain(session => session.Id != oldId);
    }

    /// <summary>Abandoning a response larger than the bounded handoff cancels and discards its session.</summary>
    [Fact(DisplayName = "Cohesion Test [Graph.Client] - Abandoned path streams discard their connection")]
    public async Task QueryPathsAsync_AbandonedEnumeration_ShouldDiscardAndPermitNewRental()
    {
        // Arrange: many paths exceed the shared one-chunk queue, while each node fits a storage page.
        await using var harness = await GraphClientTestHarness.StartAsync();
        await using (var session = await harness.Database.CreateSessionAsync(harness.Token))
        {
            await using var transaction = await session.BeginTransactionAsync(cancellationToken: harness.Token);
            for (int index = 0; index < 64; index++)
            {
                await harness.Database.CreateNodeAsync(session, ["Large"],
                    new Dictionary<string, object?> { ["index"] = index, ["payload"] = new string('x', 2_000) }, harness.Token);
            }
            await transaction.CommitAsync(harness.Token);
        }
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        Guid oldId = harness.Server.Context.Sessions.ShouldHaveSingleItem().Id;

        // Act
        await using (var paths = connection.QueryPathsAsync("MATCH (n:Large) RETURN n", cancellationToken: harness.Token).GetAsyncEnumerator())
        {
            (await paths.MoveNextAsync()).ShouldBeTrue();
            paths.Current.Nodes.ShouldHaveSingleItem().Properties["payload"].ShouldBeOfType<string>().Length.ShouldBe(2_000);
            await Should.ThrowAsync<InvalidOperationException>(async () =>
                await connection.QueryAsync("SHOW LABELS", cancellationToken: harness.Token));
        }

        // Assert
        connection.IsOpen.ShouldBeFalse();
        await using var fresh = await harness.Client.ConnectAsync(harness.Token);
        (await fresh.QueryAsync("MATCH (n:Large) RETURN n.index", cancellationToken: harness.Token)).Count.ShouldBe(64);
        harness.Server.Context.Sessions.ShouldContain(session => session.Id != oldId);
    }

    private static async Task<List<GraphPath>> CollectAsync(IAsyncEnumerable<GraphPath> paths)
    {
        var result = new List<GraphPath>();
        await foreach (var path in paths) { result.Add(path); }
        return result;
    }
}
