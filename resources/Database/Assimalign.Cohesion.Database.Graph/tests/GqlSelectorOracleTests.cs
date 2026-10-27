using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Graph.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GqlSelectorOracleTests
{
    private readonly ITestOutputHelper _output;

    public GqlSelectorOracleTests(ITestOutputHelper output) { _output = output; }

    [Fact(DisplayName = "Cohesion Test [Graph] - Selector oracle: 3,240 bag and order cases cover all selectors and modes (#1734)")]
    public async Task ExecuteAsync_IndependentBoundedOracle_ShouldPreserveBagsAndOrderAsync()
    {
        // Arrange: the oracle walks physical edges directly, without querying an unselected
        // pattern or sharing the executor's decomposition, pruning, mode or sorting code.
        string[] selectors = ["ANY", "ALL", "ANY SHORTEST", "ALL SHORTEST", "SHORTEST 3", "SHORTEST 2 GROUPS"];
        string[] modes = ["WALK", "TRAIL", "SIMPLE", "ACYCLIC"];
        string[] patterns = [
            "(a:Start)-[:T]->{0,4}(z:End)",
            "(a:Start)-[:T]->{1,4}(z:End)",
            "(a:Start)-[:T]->{2,4}(z:End)",
            "(a:Start)<-[:T]-{0,4}(z:End)",
            "(a:Start)-[:T]-{0,4}(z:End)",
            "(a:Start)-[r:T WHERE r.w=1]->{0,4}(z:End)",
            "(a:Start) ((x WHERE x.k<3)-[:T]->(y WHERE y.k<=3)){0,4} (z:End)",
            "(a:Start)-[:T]->{0,2}(x)-[:T]->{0,2}(z:End)",
            "((a:Start)-[:T]->{0,4}(z:End) WHERE path_length(p)>2)",
        ];
        int cases = 0;
        await using var engine = GraphDatabaseEngine.Create("oracle", new());
        for (int fixture = 0; fixture < 15; fixture++)
        {
            var database = await engine.CreateDatabaseAsync($"graph{fixture}", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            var nodes = new GraphNode[5];
            int target = fixture == 14 ? 0 : 3;
            for (int i = 0; i < nodes.Length; i++)
            {
                var labels = new List<string> { "V" };
                if (i == 0) { labels.Add("Start"); }
                if (i == target) { labels.Add("End"); }
                nodes[i] = await database.CreateNodeAsync(session, labels, new Dictionary<string, object?> { ["k"] = (long)i }, CancellationToken.None);
            }
            (int From, int To)[] topology = [(0,1),(1,3),(0,2),(2,3),(1,2),(2,1),(1,1),(0,1),(3,0)];
            var edges = new List<GraphRelationship>();
            for (int i = 0; i < topology.Length; i++)
            {
                var pair = topology[i];
                if ((fixture + i) % 5 == 0 || fixture == 13 && (pair.From == 3 || pair.To == 3)) { continue; }
                edges.Add(await database.CreateRelationshipAsync(session, nodes[pair.From].Id, nodes[pair.To].Id,
                    (fixture + i) % 4 == 0 ? "U" : "T", new Dictionary<string, object?> { ["w"] = (long)((fixture + i) % 3 == 0 ? 0 : 1) }, CancellationToken.None));
            }
            foreach (string mode in modes)
            {
                for (int pattern = 0; pattern < patterns.Length; pattern++)
                {
                    var paths = Enumerate(nodes, edges, target, mode, pattern);
                    foreach (string selector in selectors)
                    {
                        string prefix = fixture % 2 == 0 && pattern % 3 == 0 ? "MATCH (a:Start),(z:End) " : "";
                        string selection = selector == "SHORTEST 2 GROUPS" ? $"SHORTEST 2 {mode} GROUPS" : $"{selector} {mode}";
                        string source = $"{prefix}MATCH p = {selection} {patterns[pattern]} RETURN p";
                        var expected = Select(paths, selector).Select(Signature).ToArray();
                        // Act
                        var result = (GraphPathsQueryResult)await session.ExecuteAsync(GraphPathsQueryRequest.FromGql(source), CancellationToken.None);
                        var actual = result.Paths.Select(path => Signature(path.Relationships.Select(edge => edge.Id.Value).ToArray())).ToArray();
                        // Assert: compare multiplicity independently of the documented sequence.
                        string context = $"fixture={fixture}; {source}";
                        actual.Order(StringComparer.Ordinal).ShouldBe(expected.Order(StringComparer.Ordinal), context);
                        actual.ShouldBe(expected, context);
                        session.LastStatementMetrics!.Memory.RetainedBytes.ShouldBe(0);
                        session.LastStatementMetrics.Memory.ActiveLeases.ShouldBe(0);
                        cases++;
                    }
                }
            }
        }
        cases.ShouldBe(3240);
        _output.WriteLine($"{cases} oracle cases; {cases * 2} independent bag/order comparisons.");
    }

    private static List<ulong[]> Enumerate(GraphNode[] nodes, List<GraphRelationship> edges, int target, string mode, int pattern)
    {
        int minimum = pattern == 1 ? 1 : pattern == 2 ? 2 : pattern == 8 ? 3 : 0;
        var indexes = nodes.Select((node, index) => (node.Id, index)).ToDictionary(pair => pair.Id, pair => pair.index);
        var found = new List<ulong[]>();
        var relationships = new List<ulong>();
        var visits = new List<int> { 0 };
        Walk(0);
        return found;

        void Walk(int current)
        {
            if (current == target && relationships.Count >= minimum) { found.Add(relationships.ToArray()); }
            if (relationships.Count == 4 || mode == "SIMPLE" && visits.Count > 1 && current == 0) { return; }
            foreach (var edge in edges)
            {
                if (edge.Type != "T" || pattern == 5 && !Equals(edge.Properties["w"], 1L)) { continue; }
                int left = indexes[edge.From], right = indexes[edge.To];
                int next;
                if (pattern == 3 && current == right) { next = left; }
                else if (pattern != 3 && current == left) { next = right; }
                else if (pattern == 4 && current == right) { next = left; }
                else { continue; }
                if (pattern == 6 && (current >= 3 || next > 3)) { continue; }
                if (mode == "TRAIL" && relationships.Contains(edge.Id.Value) ||
                    mode == "ACYCLIC" && visits.Contains(next) ||
                    mode == "SIMPLE" && next != 0 && visits.Contains(next)) { continue; }
                relationships.Add(edge.Id.Value); visits.Add(next);
                Walk(next);
                relationships.RemoveAt(relationships.Count - 1); visits.RemoveAt(visits.Count - 1);
            }
        }
    }

    private static IEnumerable<ulong[]> Select(List<ulong[]> paths, string selector)
    {
        var identity = Comparer<ulong[]>.Create((left, right) =>
        {
            for (int i = 0; i < Math.Min(left.Length, right.Length); i++)
            { int result = left[i].CompareTo(right[i]); if (result != 0) { return result; } }
            return left.Length.CompareTo(right.Length);
        });
        if (selector == "ALL") { return paths.OrderBy(path => path, identity); }
        if (selector == "ANY") { return paths.OrderBy(path => path, identity).Take(1); }
        var ordered = paths.OrderBy(path => path.Length).ThenBy(path => path, identity);
        if (selector == "ANY SHORTEST") { return ordered.Take(1); }
        if (selector == "SHORTEST 3") { return ordered.Take(3); }
        var lengths = ordered.Select(path => path.Length).Distinct().Take(selector == "ALL SHORTEST" ? 1 : 2).ToHashSet();
        return ordered.Where(path => lengths.Contains(path.Length));
    }

    private static string Signature(ulong[] path) => string.Join(",", path);
}
