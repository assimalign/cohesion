using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Execution;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Tests;

/// <summary>Exercises path projection directly from engine match bindings and traversal positions.</summary>
public sealed class GraphPathsQueryTests
{
    [Theory(DisplayName = "Cohesion Test [Graph] - Named paths preserve traversal order around an indexed anchor")]
    [InlineData("MATCH p = (a:Vertex)-[:LINK]->(b:Vertex {name: 'B'})-[:LINK]->(c:Vertex) RETURN p", "A", "C")]
    [InlineData("MATCH p = (a:Vertex)<-[:LINK]-(b:Vertex {name: 'B'})<-[:LINK]-(c:Vertex) RETURN p", "C", "A")]
    public async Task ExecutePaths_IndexedMiddleAnchor_PreservesTraversalAndStoredDirections(string statement, string first, string last)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = (IGraphDatabase)await engine.CreateDatabaseAsync("paths", token);
        await using var session = await database.CreateSessionAsync(token);
        await session.ExecuteAsync("CREATE (:Vertex {name: 'A'})-[:LINK {weight: 2}]->(:Vertex {name: 'B'})-[:LINK {weight: 3}]->(:Vertex {name: 'C'})", cancellationToken: token);
        await GraphSchema.Open(database, session).CreateIndexAsync("Vertex", "by_name", "name", token);

        var result = (await session.ExecuteAsync(GraphPathsQueryRequest.FromGql(statement), token)).ShouldBeOfType<GraphPathsQueryResult>();

        result.Status.ShouldBe(QueryResultStatus.Success);
        result.AffectedCount.ShouldBe(-1);
        result.Diagnostics.ShouldBeNull();
        var path = result.Paths.Single();
        path.Nodes.Select(node => node.Properties["name"]).ShouldBe([first, "B", last]);
        path.Nodes.ShouldAllBe(node => node.Labels.Contains("Vertex"));
        path.Relationships.Select(edge => edge.Type).ShouldBe(["LINK", "LINK"]);
        var from = first == "A" ? path.Nodes[0].Id : path.Nodes[2].Id;
        var to = first == "A" ? path.Nodes[2].Id : path.Nodes[0].Id;
        var firstEdge = path.Relationships.Single(edge => Equals(edge.Properties["weight"], 2L));
        var secondEdge = path.Relationships.Single(edge => Equals(edge.Properties["weight"], 3L));
        firstEdge.From.ShouldBe(from);
        firstEdge.To.ShouldBe(path.Nodes[1].Id);
        secondEdge.From.ShouldBe(path.Nodes[1].Id);
        secondEdge.To.ShouldBe(to);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Bound entity paths preserve identities and property runtime values")]
    public async Task ExecutePaths_EntityProjection_PreservesEntitiesIncludingAnonymousEndpoints()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = (IGraphDatabase)await engine.CreateDatabaseAsync("entities", token);
        await using var session = await database.CreateSessionAsync(token);
        var properties = new Dictionary<string, object?>
        {
            ["name"] = "Alice", ["unsigned"] = ulong.MaxValue, ["small"] = (byte)7,
            ["decimal"] = 12.50m, ["flag"] = true, ["absent"] = null,
        };
        var alice = await database.CreateNodeAsync(session, ["Person", "Employee"], properties, token);
        var bob = await database.CreateNodeAsync(session, ["Person"], new Dictionary<string, object?> { ["name"] = "Bob" }, token);
        var edge = await database.CreateRelationshipAsync(session, alice.Id, bob.Id, "KNOWS", new Dictionary<string, object?> { ["weight"] = (ushort)23 }, token);

        var nodeResult = (await session.ExecuteAsync(GraphPathsQueryRequest.FromGql("MATCH (a:Employee) RETURN a AS person"), token)).ShouldBeOfType<GraphPathsQueryResult>();
        var edgeResult = (await session.ExecuteAsync(GraphPathsQueryRequest.FromGql("MATCH (:Person {name: 'Bob'})<-[r:KNOWS]-() RETURN r"), token)).ShouldBeOfType<GraphPathsQueryResult>();

        var nodePath = nodeResult.Paths.Single();
        nodePath.Relationships.ShouldBeEmpty();
        nodePath.Nodes.Single().Id.ShouldBe(alice.Id);
        nodePath.Nodes.Single().Labels.ShouldBe(alice.Labels);
        foreach (var property in properties) { nodePath.Nodes[0].Properties[property.Key].ShouldBe(property.Value); }
        var edgePath = edgeResult.Paths.Single();
        edgePath.Nodes.Select(node => node.Id).ShouldBe([alice.Id, bob.Id]);
        edgePath.Relationships.Single().Id.ShouldBe(edge.Id);
        edgePath.Relationships.Single().Properties["weight"].ShouldBeOfType<ushort>().ShouldBe((ushort)23);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Named paths retain cycles but never repeat a relationship")]
    public async Task ExecutePaths_Cycle_PreservesRepeatedNodeAndTrailSemantics()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("cycles", token);
        await using var session = await database.CreateSessionAsync(token);
        await session.ExecuteAsync("CREATE (a:Vertex {name: 'A'})-[:LINK]->(:Vertex {name: 'B'})-[:LINK]->(a)", cancellationToken: token);

        var cycle = (await session.ExecuteAsync(GraphPathsQueryRequest.FromGql("MATCH p = (a:Vertex {name: 'A'})-[:LINK]->()-[:LINK]->(a) RETURN p"), token)).ShouldBeOfType<GraphPathsQueryResult>().Paths.Single();
        var repeatedEdge = (await session.ExecuteAsync(GraphPathsQueryRequest.FromGql("MATCH p = (a:Vertex {name: 'A'})-[:LINK]->()-[:LINK]->()-[:LINK]->() RETURN p"), token)).ShouldBeOfType<GraphPathsQueryResult>();

        cycle.Nodes.Select(node => node.Properties["name"]).ShouldBe(["A", "B", "A"]);
        cycle.Nodes[0].Id.ShouldBe(cycle.Nodes[2].Id);
        cycle.Relationships.Select(edge => edge.Id).Distinct().Count().ShouldBe(2);
        repeatedEdge.Paths.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Path requests reject incompatible operations before mutation")]
    [InlineData("CREATE (:Vertex {name: 'Unexpected'})")]
    [InlineData("MATCH (a:Vertex) CREATE (:Vertex {name: 'Unexpected'}) RETURN a")]
    [InlineData("MATCH (a:Vertex) DELETE a")]
    [InlineData("MATCH (a:Vertex) DETACH DELETE a")]
    [InlineData("MATCH (a:Vertex) RETURN a.name")]
    [InlineData("MATCH (a:Vertex) RETURN a, a")]
    [InlineData("SHOW LABELS")]
    public async Task ExecutePaths_IncompatibleStatement_FailsWithoutChangingData(string statement)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("rejections", token);
        await using var session = await database.CreateSessionAsync(token);
        await session.ExecuteAsync("CREATE (:Vertex {name: 'Original'})", cancellationToken: token);

        var failure = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync(GraphPathsQueryRequest.FromGql(statement), token));

        failure.Message.ShouldContain("COHDBG001", Case.Sensitive);
        var after = (await session.ExecuteAsync(GraphPathsQueryRequest.FromGql("MATCH (a:Vertex) RETURN a"), token)).ShouldBeOfType<GraphPathsQueryResult>();
        after.Paths.Single().Nodes.Single().Properties["name"].ShouldBe("Original");
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Path variables cannot masquerade as graph entities")]
    [InlineData("MATCH p = (a) RETURN p.name")]
    [InlineData("MATCH p = (a) WHERE p.name = 'A' RETURN a.name")]
    [InlineData("MATCH p = (a) DELETE p")]
    [InlineData("MATCH p = (a), (p) RETURN a.name")]
    [InlineData("MATCH p = (p) RETURN p")]
    [InlineData("MATCH p = (a), p = (b) RETURN p")]
    public async Task Execute_PathVariableUsedAsEntity_RejectsBeforeMatching(string statement)
    {
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("binding", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        var error = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync(statement, cancellationToken: CancellationToken.None));

        error.Message.ShouldContain("COHDBG003", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Materialized paths release the operation and retain their snapshot")]
    public async Task ExecutePaths_MaterializedResult_AllowsSubsequentMutationAndRetainsSnapshot()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("snapshot", token);
        await using var session = await database.CreateSessionAsync(token);
        await session.ExecuteAsync("CREATE (:Vertex {name: 'Original'})", cancellationToken: token);
        var before = (await session.ExecuteAsync(GraphPathsQueryRequest.FromGql("MATCH p = (a:Vertex) RETURN p"), token)).ShouldBeOfType<GraphPathsQueryResult>();

        await session.ExecuteAsync("MATCH (a:Vertex) DELETE a", cancellationToken: token);
        var after = (await session.ExecuteAsync(GraphPathsQueryRequest.FromGql("MATCH (a:Vertex) RETURN a"), token)).ShouldBeOfType<GraphPathsQueryResult>();

        after.Paths.ShouldBeEmpty();
        before.Paths.Single().Nodes.Single().Properties["name"].ShouldBe("Original");
    }
}
