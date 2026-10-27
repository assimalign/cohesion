using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Graph.Internal;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GqlUnselectedMatchAllocationTests
{
    private readonly ITestOutputHelper _output;

    public GqlUnselectedMatchAllocationTests(ITestOutputHelper output) { _output = output; }

#if !DEBUG
    [Theory(DisplayName = "Cohesion Test [Graph] - Unselected MATCH retains base execution allocation budgets (#1734)")]
    // Review measurements at e0e776aac: 9,160 / 28,696 / 204,546 bytes per match.
    // The smaller fixture below measured 8,416 / 26,888 / 137,983 with the base matcher and budget layout.
    // Enforce those exact fixture baselines as well as the review's broader ceilings.
    [InlineData("MATCH (a:Start) RETURN a.k", 9160L, 8416L)]
    [InlineData("MATCH (a:Start)->(b) RETURN a.k", 28696L, 26888L)]
    [InlineData("MATCH (a:Start) ((x)->(y)){0,3} (b) RETURN a.k", 204546L, 137983L)]
    public async Task MatchAsync_UnselectedPaths_ShouldKeepBaseAllocationBudgetAsync(string source, long baseline, long fixtureCeiling)
    {
        // Arrange: keep parsing, planning, snapshot acquisition and warmup outside measurement.
        await using var engine = GraphDatabaseEngine.Create("allocation", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (a:Start {k:0})-[:T]->(b {k:1})-[:T]->(c {k:2}), " +
            "(a)-[:T]->(c), (c)-[:T]->(a), (c)-[:T]->(c), (:Other)", cancellationToken: CancellationToken.None);
        long bytes = await database.RunAsync(session, operation =>
        {
            var plan = new GraphPlanner(database, operation.Context.Snapshot).Plan(
                GraphQueryRequest.FromGql(source).Statement.GqlExpression).MatchClauses[0].Paths[0];
            void Match()
            {
                var output = new List<Dictionary<string, object?>>();
                var pending = GraphPlanExecutor.MatchAsync(database, operation, plan, new(StringComparer.Ordinal), output,
                    new GraphPlanExecutor.ExpansionBudget(), null, CancellationToken.None);
                pending.IsCompletedSuccessfully.ShouldBeTrue(); // The measured in-memory loop cannot change threads.
                pending.GetAwaiter().GetResult();
                GC.KeepAlive(output);
            }
            for (int i = 0; i < 100; i++) { Match(); }
            // Act: minimum of five warmed rounds, 100 complete matches per round.
            long minimum = long.MaxValue;
            for (int round = 0; round < 5; round++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 100; i++) { Match(); }
                minimum = Math.Min(minimum, GC.GetAllocatedBytesForCurrentThread() - before);
            }
            return ValueTask.FromResult(minimum / 100);
        }, CancellationToken.None);
        // Assert
        _output.WriteLine($"{source}: {bytes} bytes/match; review ceiling={baseline}; fixture base={fixtureCeiling}");
        bytes.ShouldBeLessThanOrEqualTo(baseline);
        bytes.ShouldBeLessThanOrEqualTo(fixtureCeiling);
    }
#endif
}
