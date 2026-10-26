using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Internal;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphAdjacencyFilterTests
{
    [Theory(DisplayName = "Cohesion Test [Graph] - Adjacency: fixed, quantified and reversed hops charge only selected edges (#1532)")]
    [InlineData("MATCH (a:Hub)-[:T]->(b) RETURN a", 3)]
    [InlineData("MATCH (a:Hub)-[:T]->{1}(b) RETURN a", 3)]
    [InlineData("MATCH (b)<-[:T]-(a:Hub {k:0}) RETURN a", 3)]
    [InlineData("MATCH (a:Hub)<-[:T]-(b) RETURN a", 33)]
    [InlineData("MATCH (a:Hub)-[:T|U]->(b) RETURN a", 35)]
    [InlineData("MATCH (a:Hub)-[:!U]->(b) RETURN a", 3)]
    [InlineData("MATCH (a:Hub)-[:T&U]->(b) RETURN a", 0)]
    [InlineData("MATCH (a:Hub)-[:!%]->(b) RETURN a", 0)]
    [InlineData("MATCH (a:Hub)-[:T]-(b) RETURN a", 35)]
    public async Task MatchAsync_FilteredHop_ShouldSeparatePhysicalAndViableCountsAsync(string source, int matches)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = GraphDatabaseEngine.Create("filter", new());
        var database = await engine.CreateDatabaseAsync("graph", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await session.ExecuteAsync("INSERT (a:Hub {k:0}), (b:Leaf {k:1}), (a)-[:T]->(b), (a)-[:T]->(b), (a)-[:T]->(a)", cancellationToken: timeout.Token);
        await session.ExecuteAsync("MATCH (a:Hub),(b:Leaf) FOR k IN [" + string.Join(',', Enumerable.Range(0, 32)) + "] INSERT (b)-[:T]->(a), (a)-[:U]->(b)", cancellationToken: timeout.Token);
        await GraphSchema.Open(database, session).CreateIndexAsync("Hub", "by_k", "k", timeout.Token);
        var budget = new GraphPlanExecutor.ExpansionBudget();
        var output = new List<Dictionary<string, object?>>();
        // Act
        await database.RunAsync(session, async operation =>
        {
            var plan = new GraphPlanner(database, operation.Context.Snapshot).Plan(GraphQueryRequest.FromGql(source).Statement.GqlExpression);
            await GraphPlanExecutor.MatchAsync(database, operation, plan.MatchClauses[0].Paths[0], new(StringComparer.Ordinal), output, budget, null, timeout.Token);
            return true;
        }, timeout.Token);
        // Assert: the only node candidate is Hub. A self-loop counts once in either direction.
        output.Count.ShouldBe(matches);
        budget.Physical.ShouldBe(matches + 1);
        budget.Viable.ShouldBe(matches);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Adjacency: unpublished INSERT edges use the same type and direction selection (#1532)")]
    public async Task MatchAsync_StatementOverlay_ShouldFilterBeforeChargingPhysicalBudgetAsync()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = GraphDatabaseEngine.Create("overlay-filter", new());
        var database = await engine.CreateDatabaseAsync("graph", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        var budget = new GraphPlanExecutor.ExpansionBudget();
        var output = new List<Dictionary<string, object?>>();
        // Act
        await database.RunAsync(session, async operation =>
        {
            await database.LockWriterAsync(operation, timeout.Token);
            var a = await operation.Updates.CreateNodeAsync(["Hub"], new Dictionary<string, object?>(), timeout.Token);
            var b = await operation.Updates.CreateNodeAsync(["Leaf"], new Dictionary<string, object?>(), timeout.Token);
            await operation.Updates.CreateRelationshipAsync(a.Id, a.Id, "T", new Dictionary<string, object?>(), timeout.Token);
            for (int i = 0; i < 32; i++)
            {
                await operation.Updates.CreateRelationshipAsync(a.Id, b.Id, "Other", new Dictionary<string, object?>(), timeout.Token);
                await operation.Updates.CreateRelationshipAsync(b.Id, a.Id, "T", new Dictionary<string, object?>(), timeout.Token);
            }
            var plan = new GraphPlanner(database, operation.Context.Snapshot).Plan(GraphQueryRequest.FromGql("MATCH (a:Hub)-[:T]->{1}(b) RETURN a").Statement.GqlExpression);
            await GraphPlanExecutor.MatchAsync(database, operation, plan.MatchClauses[0].Paths[0], new(StringComparer.Ordinal), output, budget, null, timeout.Token);
            await operation.Updates.PublishAsync(timeout.Token);
            return true;
        }, timeout.Token);
        // Assert
        output.Count.ShouldBe(1);
        budget.Physical.ShouldBe(2);
        budget.Viable.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Adjacency: stopping after the first hub result charges visited candidates only (#1532)")]
    public async Task MatchAsync_FirstHubResult_ShouldNotChargeUnvisitedSelectedEdgesAsync()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var stopped = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        await using var engine = GraphDatabaseEngine.Create("first-result-filter", new());
        var database = await engine.CreateDatabaseAsync("graph", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await session.ExecuteAsync("INSERT (a:Hub), (b:Leaf) FOR k IN [1,2,3,4,5,6,7,8,9,10] INSERT (a)-[:T]->(b)", cancellationToken: timeout.Token);
        var output = new List<Dictionary<string, object?>>();
        var budget = new GraphPlanExecutor.ExpansionBudget
        {
            OnCandidate = _ =>
            {
                // Stop at the first-result boundary through the existing sampling hook.
                // Public LIMIT still runs after eager MATCH materialization (#1142).
                if (output.Count == 1) { stopped.Cancel(); stopped.Token.ThrowIfCancellationRequested(); }
            },
        };
        // Two candidates remain: the anchor and one edge. The selected range has ten edges.
        for (int i = 0; i < GraphPlanExecutor.ExpansionBudget.PhysicalLimit - 2; i++) { budget.Consume(); }

        // Act
        await database.RunAsync(session, async operation =>
        {
            var plan = new GraphPlanner(database, operation.Context.Snapshot).Plan(GraphQueryRequest.FromGql("MATCH (a:Hub)-[:T]->(b) RETURN a LIMIT 1").Statement.GqlExpression);
            await Should.ThrowAsync<OperationCanceledException>(async () => await GraphPlanExecutor.MatchAsync(database, operation,
                plan.MatchClauses[0].Paths[0], new(StringComparer.Ordinal), output, budget, null, stopped.Token));
            return true;
        }, timeout.Token);

        // Assert: the first result was produced without charging the nine unvisited edges.
        output.ShouldHaveSingleItem();
        budget.Physical.ShouldBe(GraphPlanExecutor.ExpansionBudget.PhysicalLimit);
        budget.Viable.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Adjacency: selected dead ends still trip the physical cap as they are visited (#1532)")]
    public async Task MatchAsync_SelectedDeadEnds_ShouldRetainPhysicalSafetyCapAsync()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = GraphDatabaseEngine.Create("safety-filter", new());
        var database = await engine.CreateDatabaseAsync("graph", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await session.ExecuteAsync("INSERT (a:Hub), (b:Leaf) FOR k IN [1,2,3,4,5,6,7,8,9,10] INSERT (a)-[:T]->(b)", cancellationToken: timeout.Token);
        var budget = new GraphPlanExecutor.ExpansionBudget();
        // Exhaust most of the statement budget without constructing a million-edge fixture.
        for (int i = 0; i < GraphPlanExecutor.ExpansionBudget.PhysicalLimit - 4; i++) { budget.Consume(); }
        // Act / Assert
        await database.RunAsync(session, async operation =>
        {
            var plan = new GraphPlanner(database, operation.Context.Snapshot).Plan(GraphQueryRequest.FromGql("MATCH (a:Hub)-[:T]->(b {absent:1}) RETURN a").Statement.GqlExpression);
            var error = await Should.ThrowAsync<DatabaseException>(async () => await GraphPlanExecutor.MatchAsync(database, operation, plan.MatchClauses[0].Paths[0], new(StringComparer.Ordinal), [], budget, null, timeout.Token));
            error.Diagnostics.Single().Code.ShouldBe("CG05004");
            error.Message.ShouldContain("physical=1000001, viable=0", Case.Sensitive);
            return true;
        }, timeout.Token);
        budget.Physical.ShouldBe(GraphPlanExecutor.ExpansionBudget.PhysicalLimit + 1);
        budget.Viable.ShouldBe(0);
        await session.ExecuteAsync("INSERT (:AfterBudgetFailure)", cancellationToken: timeout.Token);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - EXPLAIN: fixed and repeated hops describe indexed selection and counter semantics (#1532)")]
    [InlineData("")]
    [InlineData("{1,2}")]
    public async Task Execute_ExplainFilteredHop_ShouldDescribeSelectionAndBudgetsAsync(string quantifier)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("explain-filter", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        // Bind the endpoint so this corpus continues to exercise adjacency rather than a type leaf.
        await using var result = (await session.ExecuteAsync("EXPLAIN MATCH (a) WITH a MATCH (a)-[:T]->" + quantifier + "(b) RETURN a", cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        var details = new List<string>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { if (Equals(row.GetValue(2), "Expand")) { details.Add((string)row.GetValue(5)!); } }
        // Assert
        details.Single().ShouldContain("adjacency=(node,direction,type_id,relationship)", Case.Sensitive);
        details.Single().ShouldContain("seek_direction=Outgoing; type_filter=T", Case.Sensitive);
        details.Single().ShouldContain("physical=visited_candidates; viable=accepted_hops; physical_limit=1000000", Case.Sensitive);
    }
}
