using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Graph.Tests;

[Collection(nameof(GraphConcurrencyCollection))]
public sealed class GqlPathFrontierAllocationTests
{
    private readonly ITestOutputHelper _output;

    public GqlPathFrontierAllocationTests(ITestOutputHelper output) => _output = output;

    [Theory(DisplayName = "Cohesion Test [Graph] - Path frontier: high fan-out retains one child at a time for fixed and quantified edges")]
    [InlineData("")]
    [InlineData("{1}")]
    public async Task MatchAsync_HighFanOut_ShouldRetainOneChildAtATimeAsync(string quantifier)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var engine = GraphDatabaseEngine.Create("frontier", new());
        var database = await engine.CreateDatabaseAsync("graph", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await session.ExecuteAsync("INSERT (:Hub {k:0}), (:Leaf {k:1})", cancellationToken: timeout.Token);
        const int fanOut = 20_000;
        string batch = "MATCH (a:Hub),(b:Leaf) FOR k IN [" + string.Join(',', Enumerable.Range(0, 1_000)) + "] INSERT (a)-[:T]->(b)";
        for (int i = 0; i < fanOut / 1_000; i++) { await session.ExecuteAsync(batch, cancellationToken: timeout.Token); }
        int peakFrontier = 0;
        int frontierSamples = 0;
        var budget = new GraphPlanExecutor.ExpansionBudget
        {
            OnFrontier = count =>
            {
                peakFrontier = Math.Max(peakFrontier, count);
                frontierSamples++;
            },
        };
        // Act
        await database.RunAsync(session, async operation =>
        {
            var plan = new GraphPlanner(database, operation.Context.Snapshot).Plan(GraphQueryRequest.FromGql(
                "MATCH (a:Hub)-[:T]->" + quantifier + "(b:Leaf {k:1}) RETURN a.k").Statement.GqlExpression);
            var output = new List<Dictionary<string, object?>>();
            await GraphPlanExecutor.MatchAsync(database, operation, plan.MatchClauses[0].Paths[0],
                new(StringComparer.Ordinal), output, budget, null, timeout.Token);
            // Every edge must descend to a child and produce a match; a rejected target
            // would bypass the trail copy and fail to exercise retained sibling states.
            output.Count.ShouldBe(fanOut);
            return true;
        }, timeout.Token);
        // Assert
        budget.Consumed.ShouldBe(fanOut + 1);
        budget.Viable.ShouldBe(fanOut);
        frontierSamples.ShouldBeGreaterThan(fanOut);
        // A one-hop DFS holds the suspended parent cursor plus at most one child,
        // regardless of fan-out, GC heap layout, JIT liveness or async thread hops.
        peakFrontier.ShouldBe(2);
        _output.WriteLine($"{fanOut} edges, quantifier '{quantifier}': peak frontier {peakFrontier}; {frontierSamples} samples");
    }
}
