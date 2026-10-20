using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Tests.TestObjects;

using Assimalign.Cohesion.Database.Graph.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GqlFixedChainOrderTests
{
    [Fact(DisplayName = "Cohesion Test [Graph] - Fixed chains: a failed deferred node map short-circuits a relationship arithmetic fault")]
    public async Task Execute_DeferredNodeBeforeRelationship_ShouldReturnSuccessfulCandidateAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("fixed-order", new());
        var database = await engine.CreateDatabaseAsync("graph");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("INSERT (:V {k:1})-[:T {w:1}]->(:V {k:2}), (:V {k:3})-[:T {w:1}]->(:V {k:3})");
        // Act
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(
            "MATCH (a:V {k:b.k})-[r:T {w:1/(a.k-b.k+1)}]->(b) RETURN a.k", cancellationToken: CancellationToken.None);
        var values = new List<long>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { values.Add(row.GetInt64(0)); }
        // Assert
        values.ShouldBe(new[] { 3L });
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Fixed chains: randomized deferred maps match the base evaluator at every anchor")]
    [InlineData(1144)]
    [InlineData(2407)]
    [InlineData(7183)]
    public async Task MatchAsync_RandomizedFixedChains_ShouldEqualBaseAtEveryAnchorAsync(int seed)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        await using var engine = GraphDatabaseEngine.Create("fixed-order", new());
        var database = await engine.CreateDatabaseAsync("graph", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        var random = new Random(seed);
        var nodes = new List<GraphNode>();
        for (int i = 0; i < 6; i++)
        {
            nodes.Add(await database.CreateNodeAsync(session, ["V"],
                new Dictionary<string, object?> { ["k"] = (long)random.Next(4) }, timeout.Token));
        }
        for (int i = 0; i < 12; i++)
        {
            await database.CreateRelationshipAsync(session, nodes[random.Next(nodes.Count)].Id, nodes[random.Next(nodes.Count)].Id,
                "T", new Dictionary<string, object?> { ["w"] = (long)random.Next(3) }, timeout.Token);
        }
        int comparisons = 0;
        int successes = 0;
        int failures = 0;
        // Act
        await database.RunAsync(session, async operation =>
        {
            for (int sample = 0; sample < 24; sample++)
            {
                int hops = 1 + sample % 3;
                var parts = new List<string>();
                for (int i = 0; i <= hops; i++)
                {
                    int reference = random.Next(hops + 1);
                    string map = sample % 4 == 0 ? "" : " {k:n" + reference + ".k}";
                    parts.Add("(n" + i + ":V" + map + ")");
                    if (i == hops) { continue; }
                    string edgeMap = sample % 4 == 0 ? "" : " {w:1/(n0.k-n" + hops + ".k+1)}";
                    string edge = "[r" + i + ":T" + edgeMap + "]";
                    parts.Add((sample % 3) switch { 0 => "-" + edge + "->", 1 => "<-" + edge + "-", _ => "-" + edge + "-" });
                }
                string source = "MATCH p = " + string.Concat(parts) + " RETURN p";
                var planned = new GraphPlanner(database, operation.Context.Snapshot).Plan(GraphQueryRequest.FromGql(source).Statement.GqlExpression);
                for (int anchor = 0; anchor <= hops; anchor++)
                {
                    var path = planned.MatchClauses[0].Paths[0] with { Anchor = new GraphAnchor(anchor, "V", null, null) };
                    var expected = await OutcomeAsync(() => FixedChainBaseMatcher.MatchAsync(database, operation, path, timeout.Token));
                    var actual = await OutcomeAsync(async () =>
                    {
                        var output = new List<Dictionary<string, object?>>();
                        await GraphPlanExecutor.MatchAsync(database, operation, path, new(StringComparer.Ordinal), output, new(), null, timeout.Token);
                        return output;
                    });
                    // Assert
                    actual.Error.ShouldBe(expected.Error, source + "; anchor=" + anchor);
                    actual.Paths.ShouldBe(expected.Paths, ignoreOrder: true, customMessage: source + "; anchor=" + anchor);
                    comparisons++;
                    if (expected.Error is null) { successes++; } else { failures++; }
                }
            }
            return true;
        }, timeout.Token);
        comparisons.ShouldBe(72);
        successes.ShouldBeGreaterThan(0);
        failures.ShouldBeGreaterThan(0);
    }

    private static async ValueTask<(string? Error, string[] Paths)> OutcomeAsync(
        Func<ValueTask<List<Dictionary<string, object?>>>> match)
    {
        try
        {
            var rows = await match();
            return (null, rows.Select(row =>
            {
                var path = (GraphPath)row["p"]!;
                return string.Join(',', path.Nodes.Select(node => node.Id)) + "/" + string.Join(',', path.Relationships.Select(edge => edge.Id));
            }).ToArray());
        }
        catch (DatabaseException error) { return (error.Diagnostics[0].Code, []); }
    }
}
