using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Protocol;
using Assimalign.Cohesion.Database.Graph.Storage;
using Assimalign.Cohesion.Database.Graph.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Graph.Tests;

/// <summary>Budget and ordering regressions for streaming adjacency consumption.</summary>
public sealed class GraphIncrementalAdjacencyTests
{
    private readonly ITestOutputHelper _output;

    public GraphIncrementalAdjacencyTests(ITestOutputHelper output) { _output = output; }

    [Fact(DisplayName = "Cohesion Test [Graph] - Adjacency: cleanup disposes every stream and retains the first failure (#1542)")]
    public async Task DisposeIncidentStreamsAsync_MultipleFailures_ShouldCloseEveryStream()
    {
        // Arrange
        var first = new InvalidOperationException("first disposal");
        var streams = new[] { new IncidentDisposalProbe(first), new IncidentDisposalProbe(new InvalidOperationException("second disposal")), new IncidentDisposalProbe() };

        // Act
        var error = await Should.ThrowAsync<InvalidOperationException>(async () => await GraphUpdates.DisposeIncidentStreamsAsync(streams));

        // Assert
        error.ShouldBeSameAs(first);
        streams.ShouldAllBe(stream => stream.Disposed);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Adjacency: wildcard first-hop allocation depends on types, not degree (#1542)")]
    public async Task StreamIncidentAsync_MultiTypeWildcard_ShouldBoundFirstHopAllocation()
    {
        // Arrange: the same twelve types, with one versus 256 relationships per type.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var engine = GraphDatabaseEngine.Create("wildcard-work", new());
        var database = await engine.CreateDatabaseAsync("graph", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await session.ExecuteAsync("INSERT (:Small), (:Large), (:Leaf)", cancellationToken: timeout.Token);
        string edges = string.Join(", ", Enumerable.Range(0, 12).Select(i => $"(a)-[:T{i}]->(b)"));
        await session.ExecuteAsync("MATCH (a:Small),(b:Leaf) INSERT " + edges, cancellationToken: timeout.Token);
        await session.ExecuteAsync("MATCH (a:Large),(b:Leaf) FOR k IN [" + string.Join(',', Enumerable.Range(0, 256)) +
            "] INSERT " + edges, cancellationToken: timeout.Token);

        // Act / Assert
        await database.RunAsync(session, async operation =>
        {
            ulong smallId = operation.Updates.GetNodes("Small", timeout.Token).Single().Id;
            ulong largeId = operation.Updates.GetNodes("Large", timeout.Token).Single().Id;
            await MeasureAsync(smallId);
            await MeasureAsync(largeId);
            long small = long.MaxValue;
            long large = long.MaxValue;
            for (int round = 0; round < 3; round++)
            {
                small = Math.Min(small, await MeasureAsync(smallId));
                large = Math.Min(large, await MeasureAsync(largeId));
            }
            large.ShouldBeLessThanOrEqualTo(small + 20_000);
            large.ShouldBeLessThan(150_000);
            _output.WriteLine($"Wildcard first hop, twelve types: small={small}, large={large} allocated bytes.");
            return true;

            async ValueTask<long> MeasureAsync(ulong nodeId)
            {
                long decoded = database.Store.IncidentRecordsDecoded;
                long before = GC.GetAllocatedBytesForCurrentThread();
                await using (var stream = operation.Updates.StreamIncidentAsync(nodeId, GraphIncidentDirection.Outgoing, null, timeout.Token)
                    .GetAsyncEnumerator(timeout.Token))
                { (await stream.MoveNextAsync()).ShouldBeTrue(); }
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                (database.Store.IncidentRecordsDecoded - decoded).ShouldBe(12L);
                return allocated;
            }
        }, timeout.Token);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Adjacency: type discovery and merge initialization obey the expansion budget (#1542)")]
    public async Task MatchAsync_FiveHundredIncidentTypes_ShouldBoundInitializationWork()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var engine = GraphDatabaseEngine.Create("type-fanout", new());
        var database = await engine.CreateDatabaseAsync("graph", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await session.ExecuteAsync("INSERT (a:Hub), (b:Leaf), " +
            string.Join(", ", Enumerable.Range(0, 500).Select(i => $"(a)-[:T{i}]->(b)")), cancellationToken: timeout.Token);
        var budget = new GraphPlanExecutor.ExpansionBudget();
        for (int i = 0; i < GraphPlanExecutor.ExpansionBudget.PhysicalLimit - 2; i++) { budget.Consume(); }
        long before = database.Store.IncidentRecordsDecoded;

        // Act
        await database.RunAsync(session, async operation =>
        {
            var plan = new GraphPlanner(database, operation.Context.Snapshot).Plan(GraphQueryRequest.FromGql(
                "MATCH (a:Hub)-[r]->(b {absent:1}) RETURN a").Statement.GqlExpression);
            var error = await Should.ThrowAsync<DatabaseException>(async () => await GraphPlanExecutor.MatchAsync(database, operation,
                plan.MatchClauses[0].Paths[0], new(StringComparer.Ordinal), [], budget, null, timeout.Token));
            error.Message.ShouldContain("candidate expansions", Case.Sensitive);
            error.Message.ShouldContain("physical=1000001", Case.Sensitive);
            return true;
        }, timeout.Token);

        // Assert
        (database.Store.IncidentRecordsDecoded - before).ShouldBe(1L);
        budget.Physical.ShouldBe(GraphPlanExecutor.ExpansionBudget.PhysicalLimit + 1);
        budget.Viable.ShouldBe(0);
    }

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

        // Assert: the three affordable heads are charged before decoding; yielded heads are not charged twice.
        (database.Store.IncidentRecordsDecoded - before).ShouldBe(3L);
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
