using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GqlPathSelectorExecutionTests
{
    private const string seed = "INSERT (a:Start {k:0})-[:T {rank:0,w:1}]->(b:V {k:1})-[:T {rank:1,w:1}]->(z:End {k:9}), " +
        "(a)-[:T {rank:2,w:1}]->(c:V {k:2})-[:T {rank:3,w:1}]->(z), " +
        "(a)-[:T {rank:4,w:1}]->(d:V {k:3})-[:T {rank:5,w:1}]->(e:V {k:4})-[:T {rank:6,w:1}]->(z), " +
        "(a)-[:T {rank:7,w:0}]->(z), (:Isolated), (s:Loop)-[:L]->(s)";

    [Theory(DisplayName = "Cohesion Test [Graph] - Selector conformance distinguishes paths from length groups and identity from length (#1734)")]
    [InlineData("ANY", "2", "0")]
    [InlineData("ALL", "2,2,3", "0,2,4")]
    [InlineData("ANY SHORTEST", "2", "0")]
    [InlineData("ALL SHORTEST", "2,2", "0,2")]
    [InlineData("SHORTEST 2 PATHS", "2,2", "0,2")]
    [InlineData("SHORTEST 2 GROUPS", "2,2,3", "0,2,4")]
    [InlineData("SHORTEST 1 GROUPS", "2,2", "0,2")]
    [InlineData("SHORTEST 9223372036854775807", "2,2,3", "0,2,4")]
    public async Task ExecuteAsync_SelectorCorpus_ShouldSelectDeterministicPathsAsync(string selector, string lengths, string firstEdges)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("selectors", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(seed, cancellationToken: CancellationToken.None);
        // Act
        var paths = await Paths(session, $"MATCH p = {selector} (a:Start)-[r:T WHERE r.w>0]->{{0,4}}(z:End) RETURN p");
        // Assert
        string.Join(",", paths.Select(path => path.Relationships.Count)).ShouldBe(lengths);
        string.Join(",", paths.Select(path => path.Relationships[0].Properties["rank"])).ShouldBe(firstEdges);
        session.LastStatementMetrics!.Memory.RetainedBytes.ShouldBe(0);
        session.LastStatementMetrics.Memory.ActiveLeases.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - ANY uses full identity order while shortest ranks length first (#1734)")]
    public async Task ExecuteAsync_AnyIdentity_ShouldDifferFromShortestAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("selectors", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(seed, cancellationToken: CancellationToken.None);
        // Act / Assert
        foreach (string bound in new[] { "{0,4}", "*" })
        {
            (await Paths(session, $"MATCH p = ANY (a:Start)-[:T]->{bound}(z:End) RETURN p")).ShouldHaveSingleItem().Relationships.Count.ShouldBe(2);
            (await Paths(session, $"MATCH p = ANY SHORTEST (a:Start)-[:T]->{bound}(z:End) RETURN p")).ShouldHaveSingleItem().Relationships.Count.ShouldBe(1);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Prefilters search for admissible paths while postfilters do not replace selected paths (#1734)")]
    public async Task ExecuteAsync_PredicatePlacement_ShouldFilterAtCorrectBoundaryAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("selectors", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(seed, cancellationToken: CancellationToken.None);
        // Act / Assert
        (await Paths(session, "MATCH p = ANY SHORTEST ((a:Start)-[:T]->{0,4}(z:End) WHERE path_length(p)>2) RETURN p"))
            .ShouldHaveSingleItem().Relationships.Count.ShouldBe(3);
        (await Paths(session, "MATCH p = ANY SHORTEST (a:Start)-[:T]->{0,4}(z:End) WHERE path_length(p)>2 RETURN p")).ShouldBeEmpty();
        (await Paths(session, "MATCH p = ANY SHORTEST (a:Start) ((x WHERE x.k<y.k)-[r:T WHERE r.w>0]->(y) WHERE x.k<y.k){0,4} (z:End) RETURN p"))
            .ShouldHaveSingleItem().Relationships[0].Properties["rank"].ShouldBe(0L);
        (await Paths(session, "MATCH p = ANY SHORTEST (a:Start) ((x)-[r:T WHERE r.w>0]->(y) WHERE x.k<y.k AND path_length(p)>2){1,4} (z:End) RETURN p"))
            .ShouldHaveSingleItem().Relationships.Count.ShouldBe(3);
        // Inline predicates also execute without a selector.
        (await Rows(session, "MATCH (a:Start WHERE a.k=0)-[r:T WHERE r.w=0]->(z:End) RETURN r.rank"))
            .ShouldHaveSingleItem()[0].ShouldBe(7L);
        var optional = await Rows(session, "MATCH (a:Start) OPTIONAL MATCH p = ANY SHORTEST (a)-[:T]->{0,4}(z:End) WHERE path_length(p)>2 RETURN z IS NULL");
        optional.ShouldHaveSingleItem()[0].ShouldBe(true);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Selectors integrate mode histories, zero paths and unbounded WALK (#1734)")]
    [InlineData("WALK", "0,1,2")]
    [InlineData("TRAIL", "0,1")]
    [InlineData("SIMPLE", "0,1")]
    [InlineData("ACYCLIC", "0")]
    public async Task ExecuteAsync_ModeAndCycle_ShouldRetainWholePathHistoryAsync(string mode, string expected)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("selectors", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(seed, cancellationToken: CancellationToken.None);
        // Act / Assert
        foreach (string body in new[] { "-[:L]->*", " ((x)-[:L]->(y)){0,} " })
        {
            var paths = await Paths(session, $"MATCH p = SHORTEST 3 {mode} GROUPS (s:Loop){body}(t:Loop) RETURN p");
            string.Join(",", paths.Select(path => path.Relationships.Count)).ShouldBe(expected);
            (await Paths(session, $"MATCH p = ANY {mode} (s:Loop){body}(t:Loop) RETURN p")).ShouldHaveSingleItem().Relationships.ShouldBeEmpty();
        }
        (await Paths(session, $"MATCH p = ALL {mode} (s:Loop)-[:L]->{{0,2}}(t:Loop) RETURN p"))
            .Select(path => path.Relationships.Count).Order().ShouldBe(expected.Split(',').Select(int.Parse));
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Endpoint pairs, incoming rows, anonymous endpoints and bound cycles select independently (#1734)")]
    public async Task ExecuteAsync_Partitions_ShouldSelectPerPairAndInputAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("selectors", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(seed, cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("MATCH (a:Start) INSERT (a)-[:T {w:1,rank:8}]->(:End {k:10})", cancellationToken: CancellationToken.None);
        // Act / Assert
        (await Rows(session, "FOR i IN [1,2] MATCH ANY SHORTEST (:Start)-[:T]->{0,4}(:End) RETURN count(*)"))
            .ShouldHaveSingleItem()[0].ShouldBe(4L);
        (await Rows(session, "MATCH (a:Start),(z:End) MATCH p = ALL SHORTEST (a)-[r:T WHERE r.w>0]->*(z) RETURN z.k,path_length(p) ORDER BY z.k"))
            .Select(row => $"{row[0]}:{row[1]}").ShouldBe(["9:2", "9:2", "10:1"]);
        (await Paths(session, "MATCH (s:Loop) MATCH p = ANY SHORTEST WALK (s)->+(s) RETURN p"))
            .ShouldHaveSingleItem().Relationships.Count.ShouldBe(1);
        (await Paths(session, "MATCH p = ANY SHORTEST WALK (:Start)->*(:Isolated) RETURN p")).ShouldBeEmpty();
        // Topology reachability respects direction/type even when a reachable cycle exists.
        (await Paths(session, "MATCH p = ANY SHORTEST WALK (:End)-[:T]->*(:Start) RETURN p")).ShouldBeEmpty();
        (await Paths(session, "MATCH p = ANY SHORTEST WALK (a:Start)->*(a) RETURN p"))
            .ShouldHaveSingleItem().Relationships.ShouldBeEmpty();
        (await Paths(session, "MATCH p = ANY SHORTEST (:End)<-[:T]-+(:Start) RETURN p")).Count.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Adjacent quantifiers deduplicate identical physical paths (#1734)")]
    public async Task ExecuteAsync_AmbiguousQuantifiers_ShouldCountDistinctPathsAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("selectors", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(seed, cancellationToken: CancellationToken.None);
        // Act
        var paths = await Paths(session, "MATCH p = SHORTEST 2 (a:Start)-[:T]->{0,2}(x)-[:T]->{0,2}(z:End) RETURN p");
        // Assert
        paths.Select(path => path.Relationships.Count).ShouldBe([1,2]);
        paths[1].Relationships[0].Properties["rank"].ShouldBe(0L);
        (await Rows(session, "MATCH p = SHORTEST 2 (a:Start)-[u:T]->{0,2}(x)-[v:T]->{0,2}(z:End) RETURN path_length(p),size(u),size(v)"))
            .Select(row => string.Join(",", row)).ShouldBe(["1,0,1", "2,0,2"]);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Selector work and retained state/output refuse with coded budgets and release grants (#1734)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_Budgets_ShouldRefuseAndReleaseAsync(bool memory)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("selectors", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(seed, cancellationToken: CancellationToken.None);
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(GraphQueryRequest.FromGql(
            "MATCH p = ALL WALK (a:Start)-[:T]->{0,4}(z:End) RETURN path_length(p)", null,
            memory ? new() { MemoryBytes = 12000 } : new() { WorkUnits = 45 }), CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(memory ? "CG05020" : "CG05004");
        session.LastStatementMetrics!.Memory.RetainedBytes.ShouldBe(0);
        session.LastStatementMetrics.Memory.ActiveLeases.ShouldBe(0);
        (await Rows(session, "RETURN 42")).ShouldHaveSingleItem()[0].ShouldBe(42L);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - All-shortest output is charged while accepting candidates are retained (#1734)")]
    public async Task ExecuteAsync_OutputMemory_ShouldRefuseAfterRetainingCandidatesAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("selectors", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:Start),(:End)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("MATCH (a:Start),(z:End) FOR i IN $xs INSERT (a)-[:T]->(z)",
            new Dictionary<string, object?> { ["xs"] = Enumerable.Range(0, 100).ToArray() }, CancellationToken.None);
        var budget = new GraphPlanExecutor.ExpansionBudget(memoryLimit: 40000);
        // Act
        await database.RunAsync(session, async operation =>
        {
            var plan = new GraphPlanner(database, operation.Context.Snapshot).Plan(GraphQueryRequest.FromGql(
                "MATCH p = ALL SHORTEST (:Start)-[:T]->(:End) RETURN p").Statement.GqlExpression, paths: true);
            var error = await Should.ThrowAsync<GraphPlanningException>(async () => await GraphPlanExecutor.MatchAsync(database,
                operation, plan.MatchClauses[0].Paths[0], new(StringComparer.Ordinal), [], budget, null, CancellationToken.None));
            // Assert: refusal occurs after accepting multiple paths, not during connectivity setup.
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05020");
            budget.SelectorCandidates.ShouldBeGreaterThan(2);
            budget.SelectorSelected.ShouldBe(0);
            budget.Memory.RetainedBytes.ShouldBe(0);
            budget.Memory.ActiveLeases.ShouldBe(0);
            return true;
        }, CancellationToken.None);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Nullable selector continuations consume memory before any search state executes (#1734)")]
    public async Task ExecuteAsync_NullableContinuations_ShouldRefuseRetainedTasksAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("selectors", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (s:Loop)-[:L]->(s)", cancellationToken: CancellationToken.None);
        string source = "MATCH p = ANY SHORTEST (a:Loop)" + string.Concat(Enumerable.Repeat("-[:L*0]->()", 512)) + " RETURN p";
        var budget = new GraphPlanExecutor.ExpansionBudget(memoryLimit: 100000);
        // Act
        await database.RunAsync(session, async operation =>
        {
            var plan = new GraphPlanner(database, operation.Context.Snapshot).Plan(
                GraphQueryRequest.FromGql(source).Statement.GqlExpression, paths: true);
            var error = await Should.ThrowAsync<GraphPlanningException>(async () => await GraphPlanExecutor.MatchAsync(database,
                operation, plan.MatchClauses[0].Paths[0], new(StringComparer.Ordinal), [], budget, null, CancellationToken.None));
            // Assert: the zero-hop shape retains tasks even though no edge can descend.
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05020");
            budget.SelectorPairs.ShouldBe(1);
            budget.SelectorStates.ShouldBe(0);
            budget.Memory.RetainedBytes.ShouldBe(0);
            budget.Memory.ActiveLeases.ShouldBe(0);
            return true;
        }, CancellationToken.None);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Selector cancellation is observed during recorded search progress and releases state (#1734)")]
    public async Task ExecuteAsync_CancelDuringSearch_ShouldReleaseStateAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("selectors", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(seed, cancellationToken: CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        int observed = 0;
        var budget = new GraphPlanExecutor.ExpansionBudget { OnConsume = count => { observed = count; if (count == 30) { cancellation.Cancel(); } } };
        // Act
        await database.RunAsync(session, async operation =>
        {
            var plan = new GraphPlanner(database, operation.Context.Snapshot).Plan(GraphQueryRequest.FromGql(
                "MATCH p = ALL (:Start)-[:T]->{0,4}(:End) RETURN p").Statement.GqlExpression, paths: true);
            await Should.ThrowAsync<OperationCanceledException>(async () => await GraphPlanExecutor.MatchAsync(database,
                operation, plan.MatchClauses[0].Paths[0], new(StringComparer.Ordinal), [], budget, null, cancellation.Token));
            return true;
        }, CancellationToken.None);
        // Assert
        observed.ShouldBeGreaterThanOrEqualTo(30);
        budget.Memory.RetainedBytes.ShouldBe(0);
        budget.Memory.ActiveLeases.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Bound equal endpoints and either-direction hops distinguish SIMPLE closure from TRAIL (#1734)")]
    [InlineData("WALK", 1)]
    [InlineData("TRAIL", 0)]
    [InlineData("SIMPLE", 1)]
    [InlineData("ACYCLIC", 0)]
    public async Task ExecuteAsync_EitherDirectionClosure_ShouldApplyModeAsync(string mode, int count)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("selectors", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:Pair {k:0})-[:P]->(:Pair {k:1})", cancellationToken: CancellationToken.None);
        // Act
        var paths = await Paths(session, $"MATCH (a:Pair {{k:0}}) MATCH p = ALL SHORTEST {mode} (a)-[:P]-{{2}}(a) RETURN p");
        // Assert
        paths.Count.ShouldBe(count);
        if (count > 0) { paths[0].Relationships[0].Id.ShouldBe(paths[0].Relationships[1].Id); }
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Selector search sees unpublished statement edges and preserves mutation semantics (#1734)")]
    public async Task ExecuteAsync_StatementOverlay_ShouldSelectAndUpdateAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("selectors", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var rows = await Rows(session, "INSERT (a:New)-[:N]->(b:New) WITH a,b MATCH p = ANY SHORTEST (a)->*(b) SET b.k=2 RETURN path_length(p),b.k");
        // Assert
        rows.ShouldHaveSingleItem().ShouldBe([1L,2L]);
        (await Rows(session, "MATCH (:New {k:2}) RETURN count(*)")).ShouldHaveSingleItem()[0].ShouldBe(1L);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - PROFILE reports actual selector states, candidates and selected paths (#1734)")]
    public async Task ExecuteAsync_Profile_ShouldReportSelectorWorkAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("selectors", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(seed, cancellationToken: CancellationToken.None);
        // Act
        await Rows(session, "MATCH p = ALL SHORTEST (:Start)-[r:T WHERE r.w>0]->{0,4}(:End) RETURN path_length(p)");
        // Assert
        session.LastStatementMetrics!.Physical.ShouldBeGreaterThan(0);
        await database.RunAsync(session, async operation =>
        {
            var budget = new GraphPlanExecutor.ExpansionBudget();
            var plan = new GraphPlanner(database, operation.Context.Snapshot).Plan(GraphQueryRequest.FromGql(
                "MATCH p = ALL SHORTEST (:Start)-[r:T WHERE r.w>0]->{0,4}(:End) RETURN p").Statement.GqlExpression, paths: true);
            await GraphPlanExecutor.MatchAsync(database, operation, plan.MatchClauses[0].Paths[0], new(StringComparer.Ordinal), [], budget, null, CancellationToken.None);
            budget.SelectorPairs.ShouldBe(1);
            budget.SelectorStates.ShouldBeGreaterThan(0);
            budget.SelectorCandidates.ShouldBe(2);
            budget.SelectorSelected.ShouldBe(2);
            await using var description = new GraphPlanner(database, operation.Context.Snapshot).Describe(plan, budget);
            bool reported = false;
            await foreach (var row in description.GetRowsAsync(CancellationToken.None))
            {
                if (!Equals(row.GetValue(2), "PathSearch")) { continue; }
                var detail = row.GetValue(5).ShouldBeOfType<string>();
                detail.ShouldContain("statement_pairs=1", Case.Sensitive);
                detail.ShouldContain("statement_candidates=2; statement_selected=2", Case.Sensitive);
                reported = true;
            }
            reported.ShouldBeTrue();
            budget.Memory.RetainedBytes.ShouldBe(0);
            return true;
        }, CancellationToken.None);
    }

    private static async Task<IReadOnlyList<GraphPath>> Paths(GraphDatabaseSession session, string source)
        => ((GraphPathsQueryResult)await session.ExecuteAsync(GraphPathsQueryRequest.FromGql(source), CancellationToken.None)).Paths;

    private static async Task<List<object?[]>> Rows(GraphDatabaseSession session, string source)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(source, cancellationToken: CancellationToken.None);
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        return rows;
    }
}
