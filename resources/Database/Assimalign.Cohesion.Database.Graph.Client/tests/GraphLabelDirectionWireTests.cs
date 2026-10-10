using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Client.Tests;

/// <summary>
/// ISO/IEC 39075 label expressions and edge directions (#1139) through the production
/// GraphDatabaseServer and Graph.Client over Connections.InMemory: scalar rows, path frames that
/// keep the stored direction, a Cypher arrow that fails as a reusable parse failure, and an
/// all-whitespace label that fails as a reusable execution failure.
/// </summary>
public sealed class GraphLabelDirectionWireTests
{
    [Fact(DisplayName = "Cohesion Test [Graph.Client] - Label expressions: (n:A|B) and -> return the expected scalar rows")]
    public async Task QueryAsync_LabelDisjunctionAndAbbreviatedEdge_ShouldReturnScalarRows()
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.ExecuteAsync(
            "CREATE (:A {name: 'a'})-[:T]->(:B {name: 'b'})-[:T]->(:C {name: 'c'}), (:A:B {name: 'ab'})",
            cancellationToken: harness.Token);

        // Act
        var nodes = await connection.QueryAsync("MATCH (n:A|B) RETURN n.name AS name", cancellationToken: harness.Token);
        var edges = await connection.QueryAsync("MATCH (n:A|B)->(m) RETURN n.name AS source, m.name AS target",
            cancellationToken: harness.Token);
        var labeled = await connection.QueryAsync("MATCH (n) WHERE n IS NOT LABELED A|B RETURN n.name",
            cancellationToken: harness.Token);

        // Assert
        nodes.Columns.Select(column => column.Name).ShouldBe(["name"]);
        nodes.Select(row => (string?)row[0]).ShouldBe(["a", "b", "ab"]);
        edges.Columns.Select(column => column.Name).ShouldBe(["source", "target"]);
        edges.Select(row => $"{row[0]}>{row[1]}").ShouldBe(["a>b", "b>c"]);
        labeled.ShouldHaveSingleItem()[0].ShouldBe("c");
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Client] - Edges: MATCH p = (a)<->(b) returns both traversals with the stored direction")]
    public async Task QueryPathsAsync_LeftOrRightEdge_ShouldKeepStoredDirection()
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync();
        GraphNode alice;
        GraphNode bob;
        GraphRelationship edge;
        await using (var session = await harness.Database.CreateSessionAsync(harness.Token))
        {
            alice = await harness.Database.CreateNodeAsync(session, ["Person"],
                new Dictionary<string, object?> { ["name"] = "Alice" }, harness.Token);
            bob = await harness.Database.CreateNodeAsync(session, ["Person"],
                new Dictionary<string, object?> { ["name"] = "Bob" }, harness.Token);
            edge = await harness.Database.CreateRelationshipAsync(session, alice.Id, bob.Id, "KNOWS", null, harness.Token);
        }
        await using var connection = await harness.Client.ConnectAsync(harness.Token);

        // Act
        var paths = new List<GraphPath>();
        await foreach (var path in connection.QueryPathsAsync("MATCH p = (a)<->(b) RETURN p", cancellationToken: harness.Token))
        {
            paths.Add(path);
        }

        // Assert: one path per traversal, and the relationship keeps Alice -> Bob in both.
        paths.Count.ShouldBe(2);
        paths[0].Nodes.Select(node => node.Id).ShouldBe([alice.Id, bob.Id]);
        paths[1].Nodes.Select(node => node.Id).ShouldBe([bob.Id, alice.Id]);
        foreach (var path in paths)
        {
            var relationship = path.Relationships.ShouldHaveSingleItem();
            relationship.Id.ShouldBe(edge.Id);
            relationship.From.ShouldBe(alice.Id);
            relationship.To.ShouldBe(bob.Id);
        }
        connection.IsOpen.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Client] - Cypher arrows: --> on QueryAsync is a GQL0008 parse failure that keeps the pooled session")]
    public async Task QueryAsync_CypherArrow_ShouldFailAsGql0008AndKeepSession()
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.ExecuteAsync("CREATE (:Person {name: 'Alice'})-[:KNOWS]->(:Person {name: 'Bob'})",
            cancellationToken: harness.Token);
        Guid sessionId = harness.Server.Sessions.ShouldHaveSingleItem().Id;

        // Act
        var error = await Should.ThrowAsync<GraphClientException>(async () =>
            await connection.QueryAsync("MATCH (a)-->(b) RETURN a.name", cancellationToken: harness.Token));

        // Assert
        error.Code.ShouldBe(ProtocolErrorCode.ParseFailure);
        error.Message.ShouldContain("GQL0008", Case.Sensitive);
        connection.IsOpen.ShouldBeTrue();
        (await connection.QueryAsync("MATCH (a:Person)->(b) RETURN b.name", cancellationToken: harness.Token))
            .ShouldHaveSingleItem()[0].ShouldBe("Bob");
        harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(sessionId);
    }

    /// <summary>
    /// An all-whitespace delimited label used to escape the engine as an uncoded argument error,
    /// which the server treats as an internal failure that ends the session. It is now a coded
    /// execution failure, so the pooled session survives and serves the next statement.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Graph.Client] - Insertion: an all-whitespace label is a COHDBG001 execution failure that keeps the pooled session")]
    public async Task ExecuteAsync_WhitespaceLabel_ShouldFailAsExecutionFailureAndKeepSession()
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.ExecuteAsync("CREATE (:Person {name: 'Alice'})", cancellationToken: harness.Token);
        Guid sessionId = harness.Server.Sessions.ShouldHaveSingleItem().Id;

        // Act
        var error = await Should.ThrowAsync<GraphClientException>(async () =>
            await connection.ExecuteAsync("INSERT (:Person&\" \" {name: 'Bob'})", cancellationToken: harness.Token));

        // Assert
        error.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        error.Message.ShouldContain("COHDBG001", Case.Sensitive);
        connection.IsOpen.ShouldBeTrue();
        (await connection.QueryAsync("MATCH (n:Person) RETURN n.name", cancellationToken: harness.Token))
            .ShouldHaveSingleItem()[0].ShouldBe("Alice");
        harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(sessionId);
    }

    /// <summary>
    /// On a path query the same parse failure reaches the caller as GQL0008. Streaming failures
    /// conservatively discard their lease (GraphPathWireTests), so a fresh rental serves the next
    /// query; the server side stays ready, as GraphServerProtocolTests proves with Ping.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Graph.Client] - Cypher arrows: --> on QueryPathsAsync is a GQL0008 parse failure")]
    public async Task QueryPathsAsync_CypherArrow_ShouldFailAsGql0008()
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.ExecuteAsync("CREATE (:Person {name: 'Alice'})-[:KNOWS]->(:Person {name: 'Bob'})",
            cancellationToken: harness.Token);

        // Act
        var error = await Should.ThrowAsync<GraphClientException>(async () =>
        {
            await foreach (var path in connection.QueryPathsAsync("MATCH (a)-->(b) RETURN a", cancellationToken: harness.Token))
            {
                path.ShouldBeNull("A Cypher arrow must not produce a path.");
            }
        });

        // Assert
        error.Code.ShouldBe(ProtocolErrorCode.ParseFailure);
        error.Message.ShouldContain("GQL0008", Case.Sensitive);
        await using var fresh = await harness.Client.ConnectAsync(harness.Token);
        (await fresh.QueryAsync("MATCH (a:Person)<-(b) RETURN b.name", cancellationToken: harness.Token))
            .ShouldHaveSingleItem()[0].ShouldBe("Alice");
    }
}
