using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GqlShortestSelectorSearchTests
{
    private readonly ITestOutputHelper _output;

    public GqlShortestSelectorSearchTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Graph] - Single shortest selectors visit grid edges linearly and preserve identity order (#1734)")]
    [InlineData(10, "ANY SHORTEST")]
    [InlineData(15, "ANY SHORTEST")]
    [InlineData(20, "ANY SHORTEST")]
    [InlineData(10, "SHORTEST 1")]
    [InlineData(15, "SHORTEST 1")]
    [InlineData(20, "SHORTEST 1")]
    public async Task MatchAsync_DirectedGrid_ShouldFindIdentityMinimalShortestWithLinearWorkAsync(int size, string selector)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("grid", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var expected = await SeedGridAsync(database, session, size);
        // Act / Assert: E = 2*n*(n-1); bound includes connectivity, filtered BFS and lazy reconstruction.
        var (paths, budget) = await MatchAsync(database, session, $"MATCH p = {selector} (:Start)-[:T]->*(:End) RETURN p");
        paths.ShouldHaveSingleItem().Relationships.Select(edge => edge.Id.Value).ShouldBe(expected[0]);
        paths[0].Relationships.Count.ShouldBe(2 * (size - 1));
        budget.Consumed.ShouldBeLessThanOrEqualTo(5L * 2 * size * (size - 1) + 128);
        budget.SelectorCandidates.ShouldBe(1);
        _output.WriteLine($"{size}x{size} {selector}: {budget.Consumed} work units; E={2 * size * (size - 1)}");
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - SHORTEST 3 stops after three identity-ordered witnesses on a 20x20 grid (#1734)")]
    public async Task MatchAsync_GridQuota_ShouldStopWithinBudgetAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("grid", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var expected = await SeedGridAsync(database, session, 20);
        // Act
        var (paths, budget) = await MatchAsync(database, session, "MATCH p = SHORTEST 3 (:Start)-[:T]->*(:End) RETURN p");
        // Assert
        paths.Count.ShouldBe(3);
        for (int i = 0; i < 3; i++) { paths[i].Relationships.Select(edge => edge.Id.Value).ShouldBe(expected[i]); }
        budget.Consumed.ShouldBeLessThanOrEqualTo(6L * 760 + 128);
        budget.SelectorCandidates.ShouldBe(3);
        _output.WriteLine($"20x20 SHORTEST 3: {budget.Consumed} work units; E=760");
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Fixed chains and repeated fixed bodies use distance pruning on the grid (#1734)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MatchAsync_FixedHopBody_ShouldAvoidEnumeratingTiesAsync(bool repeated)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("grid", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var expected = await SeedGridAsync(database, session, 20);
        string body = repeated ? " (()-[:T]->()-[:T]->()){0,} "
            : string.Concat(Enumerable.Repeat("-[:T]->()", 37)) + "-[:T]->";
        // Act
        var (paths, budget) = await MatchAsync(database, session, $"MATCH p = SHORTEST 1 (:Start){body}(:End) RETURN p");
        // Assert
        paths.ShouldHaveSingleItem().Relationships.Select(edge => edge.Id.Value).ShouldBe(expected[0]);
        budget.Consumed.ShouldBeLessThanOrEqualTo(20L * 760);
        budget.SelectorCandidates.ShouldBe(1);
        _output.WriteLine($"20x20 {(repeated ? "two-hop body" : "fixed chain")}: {budget.Consumed} work units");
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Adjacent grid quantifiers enumerate identities lazily and keep the first decomposition (#1734)")]
    public async Task MatchAsync_AmbiguousGrid_ShouldSelectIdentitiesBeforeDecompositionsAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("grid", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var expected = await SeedGridAsync(database, session, 20);
        const string match = "MATCH p = SHORTEST 3 (:Start)-[u:T]->{0,32}(x)-[v:T]->{0,32}(:End)";
        // Act
        var (paths, budget) = await MatchAsync(database, session, match + " RETURN p");
        // Assert
        paths.Count.ShouldBe(3);
        for (int i = 0; i < 3; i++) { paths[i].Relationships.Select(edge => edge.Id.Value).ShouldBe(expected[i]); }
        budget.SelectorCandidates.ShouldBe(3);
        budget.Consumed.ShouldBeLessThanOrEqualTo(100L * 760 + 512);
        _output.WriteLine($"20x20 adjacent quantifiers SHORTEST 3: {budget.Consumed} work units");
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(match + " RETURN size(u),size(v)", cancellationToken: CancellationToken.None);
        int count = 0;
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { row.GetValue(0).ShouldBe(6L); row.GetValue(1).ShouldBe(32L); count++; }
        count.ShouldBe(3);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - ALL SHORTEST on a combinatorial grid refuses cleanly without partial results (#1734)")]
    public async Task ExecuteAsync_GridAllShortest_ShouldRefuseAndReleaseAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("grid", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedGridAsync(database, session, 20);
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            GraphPathsQueryRequest.FromGql("MATCH p = ALL SHORTEST (:Start)-[:T]->*(:End) RETURN p"), CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05004");
        session.LastStatementMetrics!.Memory.RetainedBytes.ShouldBe(0);
        session.LastStatementMetrics.Memory.ActiveLeases.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Filtered distance search releases its graph on work, memory and cancellation refusal (#1734)")]
    [InlineData("work")]
    [InlineData("memory")]
    [InlineData("cancel")]
    public async Task MatchAsync_RefusedDistanceSearch_ShouldReleaseAllStateAsync(string refusal)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("grid", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedGridAsync(database, session, 10);
        using var cancellation = new CancellationTokenSource();
        var budget = new GraphPlanExecutor.ExpansionBudget(workLimit: refusal == "work" ? 350 : 1000000,
            memoryLimit: refusal == "memory" ? 50000 : 268435456)
        {
            OnConsume = count => { if (refusal == "cancel" && count == 350) { cancellation.Cancel(); } },
        };
        // Act / Assert
        await database.RunAsync(session, async operation =>
        {
            var plan = new GraphPlanner(database, operation.Context.Snapshot).Plan(GraphPathsQueryRequest.FromGql(
                "MATCH p = ANY SHORTEST (:Start)-[:T]->*(:End) RETURN p").Statement.GqlExpression, paths: true);
            async Task Match() => await GraphPlanExecutor.MatchAsync(database, operation, plan.MatchClauses[0].Paths[0],
                new(StringComparer.Ordinal), [], budget, null, cancellation.Token);
            if (refusal == "cancel") { await Should.ThrowAsync<OperationCanceledException>(Match); }
            else
            {
                var error = await Should.ThrowAsync<GraphPlanningException>(Match);
                error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(refusal == "work" ? "CG05004" : "CG05020");
            }
            return true;
        }, CancellationToken.None);
        budget.SelectorPairs.ShouldBe(1);
        budget.SelectorStates.ShouldBeGreaterThan(0);
        budget.SelectorCandidates.ShouldBe(0);
        budget.Memory.RetainedBytes.ShouldBe(0);
        budget.Memory.ActiveLeases.ShouldBe(0);
    }

    private static async Task<List<ulong[]>> SeedGridAsync(GraphDatabase database, GraphDatabaseSession session, int size)
    {
        var nodes = new GraphNode[size * size];
        var edges = new List<GraphRelationship>[nodes.Length];
        await using var transaction = await session.BeginTransactionAsync(cancellationToken: CancellationToken.None);
        for (int i = 0; i < nodes.Length; i++)
        {
            nodes[i] = await database.CreateNodeAsync(session, [i == 0 ? "Start" : i == nodes.Length - 1 ? "End" : "V"],
                cancellationToken: CancellationToken.None);
            edges[i] = [];
        }
        for (int row = 0; row < size; row++)
        {
            for (int column = 0; column < size; column++)
            {
                int i = row * size + column;
                if (column + 1 < size) { edges[i].Add(await database.CreateRelationshipAsync(session, nodes[i].Id, nodes[i + 1].Id, "T", cancellationToken: CancellationToken.None)); }
                if (row + 1 < size) { edges[i].Add(await database.CreateRelationshipAsync(session, nodes[i].Id, nodes[i + size].Id, "T", cancellationToken: CancellationToken.None)); }
            }
        }
        await transaction.CommitAsync(CancellationToken.None);
        var indexes = nodes.Select((node, index) => (node.Id, index)).ToDictionary(pair => pair.Id, pair => pair.index);
        var result = new List<ulong[]>();
        var path = new List<ulong>();
        Visit(0);
        return result;

        void Visit(int index)
        {
            if (result.Count == 3) { return; }
            if (index == nodes.Length - 1) { result.Add(path.ToArray()); return; }
            foreach (var edge in edges[index].OrderBy(edge => edge.Id.Value))
            {
                path.Add(edge.Id.Value);
                Visit(indexes[edge.To]);
                path.RemoveAt(path.Count - 1);
                if (result.Count == 3) { return; }
            }
        }
    }

    private static async Task<(List<GraphPath> Paths, GraphPlanExecutor.ExpansionBudget Budget)> MatchAsync(
        GraphDatabase database, GraphDatabaseSession session, string source)
    {
        var budget = new GraphPlanExecutor.ExpansionBudget();
        var paths = await database.RunAsync(session, async operation =>
        {
            var plan = new GraphPlanner(database, operation.Context.Snapshot).Plan(GraphPathsQueryRequest.FromGql(source).Statement.GqlExpression, paths: true);
            var output = new List<Dictionary<string, object?>>();
            await GraphPlanExecutor.MatchAsync(database, operation, plan.MatchClauses[0].Paths[0], new(StringComparer.Ordinal), output,
                budget, null, CancellationToken.None);
            return output.Select(row => (GraphPath)row["p"]!).ToList();
        }, CancellationToken.None);
        budget.Memory.RetainedBytes.ShouldBe(0);
        budget.Memory.ActiveLeases.ShouldBe(0);
        return (paths, budget);
    }
}
