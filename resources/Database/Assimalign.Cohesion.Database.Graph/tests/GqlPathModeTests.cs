using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Graph.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GqlPathModeTests
{
    private const string seed = "INSERT (a:V {k:0})-[:T]->(b:V:Pivot {k:1})-[:T]->(c:V {k:2})-[:T]->(a), " +
        "(s:Loop)-[:L]->(s), (:Pair {k:0})-[:P]->(:Pair {k:1})";

    [Theory(DisplayName = "Cohesion Test [Graph] - Modes distinguish repeated identities on cycles and self-loops (#1733)")]
    [InlineData("WALK", 6, 4)]
    [InlineData("TRAIL", 3, 1)]
    [InlineData("SIMPLE", 3, 1)]
    [InlineData("ACYCLIC", 2, 0)]
    [InlineData("", 3, 1)]
    public async Task ExecuteAsync_Cycles_ShouldEnforceWholePathMode(string mode, int cycleMax, int loopMax)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("modes", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(seed, cancellationToken: CancellationToken.None);

        // Act / Assert
        foreach (string segment in new[] { "-[:T]->{0,6}", "-[:T*0..6]->", " ((x)-[:T]->(y)){0,6} " })
        {
            var paths = await Paths(session, $"MATCH p = {mode} (a:V {{k:0}}){segment}(b) RETURN p");
            paths.Select(path => path.Relationships.Count).ShouldBe(Enumerable.Range(0, cycleMax + 1), ignoreOrder: true);
            foreach (var path in paths)
            {
                path.Nodes.Select(node => node.Properties["k"]).ShouldBe(Enumerable.Range(0, path.Nodes.Count).Select(i => (object?)(long)(i % 3)));
                for (int i = 0; i < path.Relationships.Count; i++)
                { path.Relationships[i].From.ShouldBe(path.Nodes[i].Id); path.Relationships[i].To.ShouldBe(path.Nodes[i + 1].Id); }
            }
        }
        (await Paths(session, $"MATCH p = {mode} (a:Loop)-[:L]->{{0,4}}(b) RETURN p"))
            .Select(path => path.Relationships.Count).ShouldBe(Enumerable.Range(0, loopMax + 1), ignoreOrder: true);
        session.LastStatementMetrics!.Memory.RetainedBytes.ShouldBe(0);
        session.LastStatementMetrics.Memory.ActiveLeases.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Modes span both sides of an indexed interior anchor (#1733)")]
    [InlineData("WALK", 1)]
    [InlineData("TRAIL", 1)]
    [InlineData("SIMPLE", 1)]
    [InlineData("ACYCLIC", 0)]
    public async Task ExecuteAsync_InteriorAnchor_ShouldShareIdentityState(string mode, int expected)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("modes", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(seed, cancellationToken: CancellationToken.None);
        await GraphSchema.Open(database, session).CreateIndexAsync("Pivot", "by_k", "k", CancellationToken.None);

        // Act / Assert: closure happens while expanding the left side, after the right side.
        foreach (string right in new[] { "-[:T]->{2}", " ((x)-[:T]->(y)){2} " })
        {
            string source = $"MATCH p = {mode} (a)-[:T]->(b:Pivot {{k:1}}){right}(c) RETURN p";
            await database.RunAsync(session, operation =>
            {
                var plan = new GraphPlanner(database, operation.Context.Snapshot).Plan(GraphQueryRequest.FromGql(source).Statement.GqlExpression);
                plan.MatchClauses[0].Paths[0].Anchor.NodeIndex.ShouldBe(1);
                plan.MatchClauses[0].Paths[0].Mode.ToString().ToUpperInvariant().ShouldBe(mode);
                return ValueTask.FromResult(true);
            }, CancellationToken.None);
            var paths = await Paths(session, source);
            paths.Count.ShouldBe(expected);
            if (expected != 0) { paths[0].Nodes.Select(node => node.Properties["k"]).ShouldBe(new object?[] { 0L, 1L, 2L, 0L }); }
        }
        // A partial SIMPLE cycle at the anchor cannot subsequently grow a left side.
        (await Paths(session, $"MATCH p = {mode} (a)-[:T]->(b:Pivot {{k:1}})-[:T]->{{3}}(c) RETURN p"))
            .Count.ShouldBe(mode == "WALK" ? 1 : 0);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - SIMPLE permits closing with a repeated relationship while TRAIL refuses it (#1733)")]
    [InlineData("WALK", 1)]
    [InlineData("TRAIL", 0)]
    [InlineData("SIMPLE", 1)]
    [InlineData("ACYCLIC", 0)]
    public async Task ExecuteAsync_ReusedRelationship_ShouldRespectModeAcrossSegments(string mode, int expected)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("modes", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(seed, cancellationToken: CancellationToken.None);

        // Act / Assert
        foreach (string body in new[]
        {
            "(a:Pair {k:0})-[r:P]-(b)-[r:P]-(c)",
            "(a:Pair {k:0})-[:P]-{1}(b)-[:P]-{1}(c)",
            "(a:Pair {k:0}) ((x)-[:P]-(y)){1} (b) ((u)-[:P]-(v)){1} (c)",
            "(a)-[:P]-(b:Pair {k:1})-[:P]-(c)",
        })
        {
            var paths = await Paths(session, $"MATCH p = {mode} {body} RETURN p");
            paths.Count.ShouldBe(expected);
            if (expected != 0)
            {
                paths[0].Nodes[0].Id.ShouldBe(paths[0].Nodes[^1].Id);
                paths[0].Relationships[0].Id.ShouldBe(paths[0].Relationships[1].Id);
            }
        }
        // Distinct paths get distinct uniqueness scopes, even within one MATCH.
        (await Rows(session, $"MATCH {mode} (a:Pair {{k:0}})-[:P]->(b), {mode} (a)-[:P]->(b) RETURN count(*)"))
            .ShouldHaveSingleItem()[0].ShouldBe(1L);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Mode state backtracks across siblings and SIMPLE forbids an internal repetition (#1733)")]
    public async Task ExecuteAsync_BranchingAndInternalRepeat_ShouldBacktrackWithoutStateLeaks()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("modes", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (a:Root {k:0})-[:T]->(b {k:1})-[:T]->(c {k:2})-[:T]->(b), (a)-[:T]->(d {k:3})-[:T]->(c), (c)-[:T]->(a)", cancellationToken: CancellationToken.None);
        // Act / Assert
        foreach (string mode in new[] { "TRAIL", "SIMPLE", "ACYCLIC", "WALK" })
        {
            var paths = await Paths(session, $"MATCH p = {mode} (a:Root)-[:T]->{{3}}(b) RETURN p");
            paths.Count.ShouldBe(mode == "ACYCLIC" ? 1 : mode == "SIMPLE" ? 3 : 4);
            if (mode == "ACYCLIC")
            { paths[0].Nodes.Select(node => node.Properties["k"]).ShouldBe(new object?[] { 0L, 3L, 2L, 1L }); }
            foreach (var path in paths.Where(_ => mode is "SIMPLE" or "ACYCLIC"))
            { path.Nodes.Take(path.Nodes.Count - 1).Select(node => node.Id).Distinct().Count().ShouldBe(path.Nodes.Count - 1); }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Direct ASTs validate explicit modes before execution (#1733)")]
    [InlineData("MATCH WALK (a)->+(b) RETURN a", "CK00001")]
    [InlineData("MATCH (TRAIL (a)->(b)) RETURN a", "CK00001")]
    public void Create_DirectAst_ShouldRefuseDeferredModeForms(string source, string code)
    {
        // Arrange: assemble source-free trees to exercise the independent shape gate.
        var node = new GqlNodePattern(null, [], new Dictionary<string, GqlExpression>());
        var edge = new GqlRelationshipPattern(null, null, GqlPatternDirection.Outgoing, new Dictionary<string, GqlExpression>());
        var body = new GqlPathPattern([node, node], [edge]);
        GqlPathPattern path = source.Contains("WALK", StringComparison.Ordinal)
            ? body with { Mode = GqlPathMode.Walk, Relationships = [edge with { Quantifier = new(1, null) }] }
            : new([], []) { Elements = [new GqlParenthesizedPathPattern(body with { Mode = GqlPathMode.Trail }, null)] };
        // Act / Assert
        Should.Throw<GraphPlanningException>(() => GraphPathShape.Create(path)).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
        Should.Throw<GraphPlanningException>(() => GraphPathShape.Create(body with { Mode = (GqlPathMode)255 })).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05001");
        Should.Throw<GraphPlanningException>(() => GraphPathShape.Create(body with { Mode = GqlPathMode.Walk }, creating: true)).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05001");
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Mode results equal complete-path identity filtering across anchors and zero-length groups (#1733)")]
    [InlineData("(a:Pivot {k:1})-[:T]->{0,4}(b)")]
    [InlineData("(a)-[:T]->{0,4}(b:Pivot {k:1})")]
    [InlineData("(a)-[:T]->{0,2}(b:Pivot {k:1})-[:T]->{0,2}(c)")]
    [InlineData("(a) ((x)-[:T]->(y)){0,2} (b:Pivot {k:1}) ((u)-[:T]->(v)){0,2} (c)")]
    public async Task ExecuteAsync_SeveralAnchors_ShouldAgreeWithCompletePathIdentityRules(string body)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("modes", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (a {k:0})-[:T]->(b:Pivot {k:1})-[:T]->(c {k:2})-[:T]->(a), " +
            "(a)-[:T]->(a), (b)-[:T]->(b), (c)-[:T]->(c), (b)-[:T]->(a), (c)-[:T]->(b)", cancellationToken: CancellationToken.None);
        await GraphSchema.Open(database, session).CreateIndexAsync("Pivot", "by_k", "k", CancellationToken.None);
        var walks = await Paths(session, $"MATCH p = WALK {body} RETURN p");

        // Act / Assert: filter complete WALK results by the mathematical identity contract,
        // independently of the executor's incremental acceptance and direction of expansion.
        foreach (string mode in new[] { "TRAIL", "SIMPLE", "ACYCLIC" })
        {
            var expected = walks.Where(path => mode switch
            {
                "TRAIL" => path.Relationships.Select(edge => edge.Id).Distinct().Count() == path.Relationships.Count,
                "ACYCLIC" => path.Nodes.Select(node => node.Id).Distinct().Count() == path.Nodes.Count,
                _ => path.Nodes.Select(node => node.Id).Distinct().Count() == path.Nodes.Count ||
                    path.Nodes[0].Id == path.Nodes[^1].Id && path.Nodes.Take(path.Nodes.Count - 1).Select(node => node.Id).Distinct().Count() == path.Nodes.Count - 1,
            }).Select(Key).Order(StringComparer.Ordinal).ToArray();
            var actual = await Paths(session, $"MATCH p = {mode} {body} RETURN p");
            actual.Select(Key).Order(StringComparer.Ordinal).ShouldBe(expected);
        }
        static string Key(GraphPath path) => string.Join(',', path.Nodes.Select(node => node.Id)) + "/" + string.Join(',', path.Relationships.Select(edge => edge.Id));
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Node-unique modes retain the qualifying 65th-hop refusal (#1733)")]
    [InlineData("SIMPLE")]
    [InlineData("ACYCLIC")]
    public async Task ExecuteAsync_UnboundedNodeMode_ShouldPreserveHopRefusal(string mode)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("hops", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:Start)" + string.Concat(Enumerable.Repeat("-[:T]->()", 63)) + "-[:T]->(:End)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("MATCH (n:End) INSERT (n)-[:T]->()", cancellationToken: CancellationToken.None);
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            $"MATCH {mode} (a:Start)-[:T]->+(b) WHERE b.k=-1 RETURN b", cancellationToken: CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05004");
        session.LastStatementMetrics!.Memory.RetainedBytes.ShouldBe(0);
        session.LastStatementMetrics.Memory.ActiveLeases.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Every mode preserves coded work and retained-memory refusals (#1733)")]
    [InlineData("WALK")]
    [InlineData("TRAIL")]
    [InlineData("SIMPLE")]
    [InlineData("ACYCLIC")]
    public async Task MatchAsync_BudgetFailure_ShouldReleaseModeState(string mode)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("budgets", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:Start)" + string.Concat(Enumerable.Repeat("-[:T]->()", 64)), cancellationToken: CancellationToken.None);
        // Act / Assert
        foreach (bool memory in new[] { false, true })
        {
            var budget = new GraphPlanExecutor.ExpansionBudget(workLimit: memory ? 1000 : 3, memoryLimit: memory ? 131072 : 1024 * 1024);
            await database.RunAsync(session, async operation =>
            {
                var plan = new GraphPlanner(database, operation.Context.Snapshot).Plan(GraphQueryRequest.FromGql(
                    $"MATCH {mode} (a:Start)-[:T]->{{64}}(b) RETURN b").Statement.GqlExpression);
                var error = await Should.ThrowAsync<GraphPlanningException>(async () => await GraphPlanExecutor.MatchAsync(database, operation,
                    plan.MatchClauses[0].Paths[0], new(StringComparer.Ordinal), [], budget, null, CancellationToken.None));
                error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(memory ? "CG05020" : "CG05004");
                return true;
            }, CancellationToken.None);
            budget.Consumed.ShouldBeGreaterThan(1);
            budget.Memory.RetainedBytes.ShouldBe(0);
            budget.Memory.ActiveLeases.ShouldBe(0);
        }
    }

    private static async Task<IReadOnlyList<GraphPath>> Paths(GraphDatabaseSession session, string source)
    {
        var result = (GraphPathsQueryResult)await session.ExecuteAsync(GraphPathsQueryRequest.FromGql(source), CancellationToken.None);
        return result.Paths;
    }

    private static async Task<List<object?[]>> Rows(GraphDatabaseSession session, string source)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(source, cancellationToken: CancellationToken.None);
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        return rows;
    }
}
