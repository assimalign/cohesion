using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Protocol;
using Assimalign.Cohesion.Database.Graph.Storage;

namespace Assimalign.Cohesion.Database.Graph.Tests;

/// <summary>Budget and ordering regressions for streaming adjacency consumption.</summary>
public sealed class GraphIncrementalAdjacencyTests
{
    [Fact(DisplayName = "Cohesion Test [Graph] - Adjacency: budget refusal stops decoding a wide range and closes parent cursors (#1542)")]
    public async Task MatchAsync_BudgetRefusal_ShouldStopIncidentDecodingIncrementally()
    {
        // Arrange: two selected ranges, with interleaved identities and thousands of entries.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var engine = GraphDatabaseEngine.Create("incremental-adjacency", new());
        var database = await engine.CreateDatabaseAsync("graph", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await session.ExecuteAsync("INSERT (:Hub), (:Leaf)", cancellationToken: timeout.Token);
        string batch = "MATCH (a:Hub),(b:Leaf) FOR k IN [" + string.Join(',', Enumerable.Range(0, 1_000)) +
            "] INSERT (a)-[:T]->(b), (a)-[:U]->(b)";
        for (int i = 0; i < 4; i++) { await session.ExecuteAsync(batch, cancellationToken: timeout.Token); }
        var budget = new GraphPlanExecutor.ExpansionBudget();
        for (int i = 0; i < GraphPlanExecutor.ExpansionBudget.PhysicalLimit - 4; i++) { budget.Consume(); }
        long before = database.Store.IncidentRecordsDecoded;

        // Act
        await database.RunAsync(session, async operation =>
        {
            var plan = new GraphPlanner(database, operation.Context.Snapshot).Plan(GraphQueryRequest.FromGql(
                "MATCH (a:Hub)-[:T|U]-(b {absent:1}) RETURN a").Statement.GqlExpression);
            await Should.ThrowAsync<DatabaseException>(async () => await GraphPlanExecutor.MatchAsync(database, operation,
                plan.MatchClauses[0].Paths[0], new(StringComparer.Ordinal), [], budget, null, timeout.Token));
            return true;
        }, timeout.Token);

        // Assert: four visited edges plus a head per selected nonempty range, instead of 8,000.
        (database.Store.IncidentRecordsDecoded - before).ShouldBeInRange(4L, 6L);
        budget.Physical.ShouldBe(GraphPlanExecutor.ExpansionBudget.PhysicalLimit + 1);
        budget.Viable.ShouldBe(0);
        await session.ExecuteAsync("INSERT (:AfterRefusal)", cancellationToken: timeout.Token);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Adjacency: streaming merge preserves identity order, overlay changes and self-loop uniqueness (#1542)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamIncidentAsync_InterleavedTypesAndOverlay_ShouldMatchMaterializedOrder(bool selectType)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = GraphDatabaseEngine.Create("ordered-adjacency", new());
        var database = await engine.CreateDatabaseAsync("graph", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await session.ExecuteAsync("INSERT (a:Hub), (b:Leaf), (a)-[:U]->(b), (b)-[:T]->(a), (a)-[:T]->(a), (a)-[:U]->(a)",
            cancellationToken: timeout.Token);

        // Act / Assert
        await database.RunAsync(session, async operation =>
        {
            await database.LockWriterAsync(operation, timeout.Token);
            var hub = operation.Updates.GetNodes("Hub", timeout.Token).Single();
            var leaf = operation.Updates.GetNodes("Leaf", timeout.Token).Single();
            await operation.Updates.CreateRelationshipAsync(new GraphNodeId(hub.Id), new GraphNodeId(leaf.Id), "T",
                new Dictionary<string, object?>(), timeout.Token);
            await operation.Updates.CreateRelationshipAsync(new GraphNodeId(hub.Id), new GraphNodeId(hub.Id), "U",
                new Dictionary<string, object?>(), timeout.Token);
            Guid[]? types = selectType
                ? [database.Catalog.FindRelationshipType("T", operation.Context.Snapshot)!.Value.Id] : null;
            var expected = await operation.Updates.GetIncidentAsync(hub.Id, GraphIncidentDirection.Both, types, timeout.Token);
            var found = new List<ulong>();
            await foreach (var edge in operation.Updates.StreamIncidentAsync(hub.Id, GraphIncidentDirection.Both, types, timeout.Token))
            { found.Add(edge.Id); }
            found.ShouldBe(expected.Select(edge => edge.Id));
            found.Distinct().Count().ShouldBe(found.Count);
            await operation.Updates.PublishAsync(timeout.Token);
            return true;
        }, timeout.Token);
    }
}
