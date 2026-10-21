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
    public async Task MatchAsync_HighFanOut_ShouldBoundLiveSearchAllocationsAsync(string quantifier)
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
        long baseline = 0;
        long peak = 0;
        int samples = 0;
        var budget = new GraphPlanExecutor.ExpansionBudget
        {
            OnCandidate = count =>
            {
                // The first candidate has primed the bounded incident streams. Measure
                // additional live search state as those streams advance, excluding setup.
                if (count == 1) { baseline = GC.GetTotalMemory(forceFullCollection: true); }
                else if (count % 5_000 == 0)
                {
                    peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: true) - baseline);
                    samples++;
                }
            },
        };
        // Act
        await database.RunAsync(session, async operation =>
        {
            var plan = new GraphPlanner(database, operation.Context.Snapshot).Plan(GraphQueryRequest.FromGql(
                "MATCH (a:Hub)-[:T]->" + quantifier + "(b:Leaf {k:-1}) RETURN a.k").Statement.GqlExpression);
            var output = new List<Dictionary<string, object?>>();
            await GraphPlanExecutor.MatchAsync(database, operation, plan.MatchClauses[0].Paths[0],
                new(StringComparer.Ordinal), output, budget, null, timeout.Token);
            output.ShouldBeEmpty();
            return true;
        }, timeout.Token);
        // Assert
        budget.Consumed.ShouldBe(fanOut + 1);
        samples.ShouldBe(4);
        peak.ShouldBeLessThan(2_000_000);
        _output.WriteLine($"{fanOut} edges, quantifier '{quantifier}': {peak} additional live bytes; {samples} samples");
    }
}
