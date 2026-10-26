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

public sealed class GqlQuantifiedPathTests
{
    private const string chain = "INSERT (a:V {name:'A',k:1})-[:T {w:1}]->(b:V {name:'B',k:1})-[:T {w:1}]->(c:V {name:'C',k:1})-[:T {w:2}]->(:V {name:'D',k:2})";

    [Theory(DisplayName = "Cohesion Test [Graph] - Quantified paths: ISO, Cypher and parenthesized spellings enumerate the same chain trails")]
    [InlineData("-[:T]->{1,3}")]
    [InlineData("-[:T*1..3]->")]
    [InlineData(" ((x)-[:T]->(y)){1,3} ")]
    public async Task Execute_ChainQuantifiers_ShouldReturnEveryLengthAsync(string segment)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("quantified", new());
        var database = await engine.CreateDatabaseAsync("graph");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync(chain);
        // Act
        var rows = await Rows(session, "MATCH (a:V {name:'A'})" + segment + "(b) RETURN a.name,b.name");
        // Assert
        rows.Select(row => row[1]).ShouldBe(new object?[] { "B", "C", "D" }, ignoreOrder: true);
        (await Rows(session, "MATCH (a:V {name:'A'})-[:T {w:1}]->{1,3}(b) RETURN b.name")).Select(row => row[0]).ShouldBe(new object?[] { "B", "C" }, ignoreOrder: true);
        (await Rows(session, "MATCH (a:V {name:'A'}) ((x:V {k:1})-[:T {w:1}]->(y:V {k:1})){2} (b) RETURN b.name"))
            .ShouldHaveSingleItem()[0].ShouldBe("C");
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Quantified paths: zero-length branches conjoin outer endpoints and survive unknown inner names")]
    [InlineData("-[:Missing]->{0,2}")]
    [InlineData(" ((x:Missing)-[:Missing]->(y:Missing)){0,2} ")]
    [InlineData("-[*0]->")]
    public async Task Execute_ZeroLength_ShouldPreserveRowsAndEndpointConstraintsAsync(string segment)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("quantified", new());
        var database = await engine.CreateDatabaseAsync("graph");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync(chain);
        // Act / Assert
        (await Rows(session, "MATCH (a:V {name:'A'})" + segment + "(b:V {name:'A'}) RETURN a.name,b.name"))
            .ShouldHaveSingleItem().ShouldBe(new object?[] { "A", "A" });
        (await Rows(session, "MATCH (a:V {name:'A'})" + segment + "(b:V {name:'B'}) RETURN a.name")).ShouldBeEmpty();
        var paths = (GraphPathsQueryResult)await session.ExecuteAsync(GraphPathsQueryRequest.FromGql(
            "MATCH p = (a:V {name:'A'})" + segment + "(b) RETURN p"));
        paths.Paths.ShouldHaveSingleItem().Relationships.ShouldBeEmpty();
        paths.Paths[0].Nodes.ShouldHaveSingleItem().Properties["name"].ShouldBe("A");
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Quantified paths: cycles, self-loops and diamonds enumerate relationship-unique trails")]
    public async Task Execute_CyclesAndDiamond_ShouldUseEachRelationshipOnceAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("quantified", new());
        var database = await engine.CreateDatabaseAsync("graph");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("INSERT (a:Cycle {name:'A'})-[:T]->(b:Cycle {name:'B'})-[:T]->(c:Cycle {name:'C'})-[:T]->(a), (s:Loop)-[:T]->(s), (p:Diamond {name:'P'})-[:T]->(q:Diamond {name:'Q'})-[:T]->(z:Diamond {name:'Z'}), (p)-[:T]->(:Diamond {name:'R'})-[:T]->(z)");
        // Act / Assert
        foreach (string quantifier in new[] { "+", "{1,8}", "*" })
        {
            var paths = (GraphPathsQueryResult)await session.ExecuteAsync(GraphPathsQueryRequest.FromGql("MATCH p = (a:Cycle {name:'A'})-[:T]->" + quantifier + "(b) RETURN p"));
            paths.Paths.Select(path => path.Relationships.Count).ShouldBe(quantifier == "*" ? new[] { 0, 1, 2, 3 } : new[] { 1, 2, 3 }, ignoreOrder: true);
            foreach (var path in paths.Paths) { path.Relationships.Select(edge => edge.Id).Distinct().Count().ShouldBe(path.Relationships.Count); }
        }
        (await Rows(session, "MATCH (a:Loop)-[:T]->+(b) RETURN count(*)")).ShouldHaveSingleItem()[0].ShouldBe(1L);
        (await Rows(session, "MATCH (a:Loop)-[:T]->{0,1}(b) RETURN count(*)")).ShouldHaveSingleItem()[0].ShouldBe(2L);
        (await Rows(session, "MATCH (a:Loop)-[:T]->*(b)-[:T]->*(c) RETURN count(*)")).ShouldHaveSingleItem()[0].ShouldBe(3L);
        (await Rows(session, "MATCH (a:Cycle {name:'A'})-[:T]->(b)-[:T]->+(c) RETURN count(*)")).ShouldHaveSingleItem()[0].ShouldBe(2L);
        (await Rows(session, "MATCH (a:Cycle {name:'A'})-[:T]->{1,3}(a) RETURN a.name")).ShouldHaveSingleItem()[0].ShouldBe("A");
        var diamond = (GraphPathsQueryResult)await session.ExecuteAsync(GraphPathsQueryRequest.FromGql("MATCH p = (a:Diamond {name:'P'})-[:T]->{2}(b:Diamond {name:'Z'}) RETURN p"));
        diamond.Paths.Select(path => path.Nodes[1].Properties["name"]).ShouldBe(new object?[] { "Q", "R" }, ignoreOrder: true);
        // Per-path uniqueness deliberately permits reuse by another comma-separated path.
        (await Rows(session, "MATCH (a:Loop)-[:T]->+(b),(c:Loop)-[:T]->+(d) RETURN count(*)")).ShouldHaveSingleItem()[0].ShouldBe(1L);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Quantified paths: group lists preserve source order, kinds, star scope and aggregate flow")]
    public async Task Execute_GroupVariables_ShouldCarryListsThroughStagesAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("quantified", new());
        var database = await engine.CreateDatabaseAsync("graph");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync(chain);
        // Act / Assert
        (await Rows(session, "MATCH (a:V {name:'A'})-[:T]->{,}(b) RETURN b.name"))
            .Select(row => row[0]).ShouldBe(new object?[] { "A", "B", "C", "D" }, ignoreOrder: true);
        var star = (await Rows(session, "MATCH (a:V {name:'A'}) ((x)-[r:T]->(y)){2} (b) RETURN *")).ShouldHaveSingleItem();
        star.Length.ShouldBe(5);
        star[1].ShouldBeOfType<object?[]>().Select(value => value.ShouldBeOfType<GraphNode>().Properties["name"]).ShouldBe(new object?[] { "A", "B" });
        star[2].ShouldBeOfType<object?[]>().Length.ShouldBe(2);
        star[3].ShouldBeOfType<object?[]>().Select(value => value.ShouldBeOfType<GraphNode>().Properties["name"]).ShouldBe(new object?[] { "B", "C" });
        foreach (string loop in new[] { "UNWIND r AS e", "FOR e IN r" })
        {
            (await Rows(session, "MATCH (a:V {name:'A'})-[r:T]->{2}(b) WITH * " + loop + " WITH e MATCH ()-[e:T]->() RETURN e.w"))
                .Select(row => row[0]).ShouldBe(new object?[] { 1L, 1L });
        }
        (await Rows(session, "MATCH (a:V {name:'A'})-[r:T]->{2}(b) RETURN collect_list(r)"))
            .ShouldHaveSingleItem()[0].ShouldBeOfType<object?[]>().ShouldHaveSingleItem().ShouldBeOfType<object?[]>().Length.ShouldBe(2);
        (await Rows(session, "MATCH (a:V {name:'A'})-[r:T]->*(b:V {name:'A'}) RETURN r"))
            .ShouldHaveSingleItem()[0].ShouldBeOfType<object?[]>().ShouldBeEmpty();
        (await Rows(session, "MATCH (a:V {name:'A'})-[r:T]->{3}(b) RETURN r[0].w,r[-1].w"))
            .ShouldHaveSingleItem().ShouldBe(new object?[] { 1L, 2L });
        (await Rows(session, "MATCH (a:V {name:'A'}) ((x)-[:T]->(y)){2} (b) WITH x FOR n IN x MATCH (n) RETURN n.name"))
            .Select(row => row[0]).ShouldBe(new object?[] { "A", "B" }, ignoreOrder: true);
        (await session.ExecuteAsync("MATCH (a:V {name:'A'})-[r:T]->{2}(b) WITH r UNWIND r AS e DELETE e")).AffectedCount.ShouldBe(2);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Quantified paths: invalid group use and mixed bindings fail statically")]
    [InlineData("MATCH (a)-[r:T]->{2}(b) RETURN r.w")]
    [InlineData("MATCH (a)-[r:T]->{2}(b) RETURN COALESCE(r,r).w")]
    [InlineData("MATCH (a)-[r:T]->{2}(b) RETURN [r][0].w")]
    [InlineData("MATCH (a)-[r:T]->{2}(b) RETURN {rs:r}.rs.w")]
    [InlineData("MATCH (a)-[r:T]->{2}(b) WITH r RETURN r.w")]
    [InlineData("MATCH (a)-[r:T]->{2}(b) WITH r AS rs RETURN rs.w")]
    [InlineData("MATCH (a)-[r:T]->{2}(b) WITH COALESCE(r,r) AS rs RETURN rs.w")]
    [InlineData("MATCH (a)-[r:T]->{2}(b) WITH CASE WHEN TRUE THEN r ELSE r END AS rs RETURN rs.w")]
    [InlineData("MATCH (a)-[r:T]->{2}(b) WITH NULLIF(r,NULL) AS rs RETURN rs.w")]
    [InlineData("MATCH (a)-[r:T]->{2}(b) WITH [r][0] AS rs RETURN rs.w")]
    [InlineData("MATCH (a)-[r:T]->{2}(b) WITH {rs:r}.rs AS rs RETURN rs.w")]
    [InlineData("MATCH (a)-[r:T]->{2}(b) WITH COALESCE(r,r) AS rs MATCH ((x)-[:T {w:rs[0].w}]->(y)){2} RETURN count(*)")]
    [InlineData("MATCH (a)-[r:T]->{2}(b) WITH {rs:r} AS box MATCH ((x)-[:T {w:box.rs[0].w}]->(y)){2} RETURN count(*)")]
    [InlineData("MATCH (a)-[r:T]->{2}(b) RETURN r AS rs ORDER BY rs.w")]
    [InlineData("MATCH (a)-[r:T]->{2}(b) RETURN r AS rs ORDER BY COALESCE(rs,rs).w")]
    [InlineData("MATCH (a)-[r:T]->{2}(b) RETURN r AS rs ORDER BY [rs][0].w")]
    [InlineData("MATCH (a)-[r:T]->{2}(b) WHERE r.w>1 RETURN a")]
    [InlineData("MATCH (a)-[r:T]->{2}(b) DELETE r")]
    [InlineData("MATCH (r)-[r:T]->{2}(b) RETURN b")]
    [InlineData("MATCH (a)-[r:T]->{2}(r) RETURN a")]
    [InlineData("MATCH ((x)-[r:T {w:y.k}]->(y)){2} RETURN r")]
    [InlineData("MATCH (a)-[r:T]->{2}(b) WITH r AS rs MATCH ((x)-[:T {w:rs[0].w}]->(y)){1,2} RETURN count(*)")]
    public async Task Execute_InvalidGroupUse_ShouldReportKindDiagnosticAsync(string source)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("quantified", new());
        var database = await engine.CreateDatabaseAsync("graph");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync(chain);
        // Act / Assert
        foreach (string prefix in new[] { "", "EXPLAIN " })
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(prefix + source));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG03003");
        }
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Quantified paths: right and middle anchors preserve paths, groups and deferred maps")]
    public async Task Execute_IndexedAnchors_ShouldEqualScansAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("quantified", new());
        var database = await engine.CreateDatabaseAsync("graph");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync(chain);
        string[] sources = [
            "MATCH p = (a)-[:T]->{1,3}(b:V {name:'C'}) RETURN p",
            "MATCH p = (a)-[:T]->{1,2}(b:V {name:'C'})-[:T]->(c) RETURN p",
            "MATCH p = (a) ((x)-[:T]->(y)){1,3} (b:V {name:'C'}) RETURN p",
            "MATCH p = (a)-[:T {w:b.k}]->{2}(b:V {name:'C'}) RETURN p",
        ];
        var expected = new List<string[]>();
        foreach (string source in sources) { expected.Add(await PathKeys(session, source)); }
        await GraphSchema.Open(database, session).CreateIndexAsync("V", "by_name", "name");
        // Act / Assert
        for (int i = 0; i < sources.Length; i++)
        {
            (await PathKeys(session, sources[i])).OrderBy(key => key, StringComparer.Ordinal).ShouldBe(expected[i].OrderBy(key => key, StringComparer.Ordinal));
            await database.RunAsync(session, operation =>
            {
                var plan = new GraphPlanner(database, operation.Context.Snapshot).Plan(GraphQueryRequest.FromGql(sources[i]).Statement.GqlExpression);
                plan.MatchClauses[0].Paths[0].Anchor.NodeIndex.ShouldBe(1);
                return ValueTask.FromResult(true);
            }, CancellationToken.None);
        }
        var groups = (await Rows(session, "MATCH (a) ((x)-[:T]->(y)){2} (b:V {name:'C'}) RETURN x,y")).ShouldHaveSingleItem();
        groups[0].ShouldBeOfType<object?[]>().Select(value => value.ShouldBeOfType<GraphNode>().Properties["name"]).ShouldBe(new object?[] { "A", "B" });
        groups[1].ShouldBeOfType<object?[]>().Select(value => value.ShouldBeOfType<GraphNode>().Properties["name"]).ShouldBe(new object?[] { "B", "C" });
        (await Rows(session, "MATCH (a) ((x:V {name:'A'})-[:T]->(y)){0,1} (b) RETURN count(*)"))
            .ShouldHaveSingleItem()[0].ShouldBe(5L);
        (await Rows(session, "MATCH (b:V {name:'C'}) WITH b MATCH (a)-[:T]->{1,3}(b) RETURN a.name"))
            .Select(row => row[0]).ShouldBe(new object?[] { "B", "A" }, ignoreOrder: true);
        (await Rows(session, "OPTIONAL MATCH (b:Missing) MATCH (a)-[:T]->{0,2}(b) RETURN count(*)"))
            .ShouldHaveSingleItem()[0].ShouldBe(0L);
        (await Rows(session, "MATCH (a)-[:T {w:c.k}]->{2}(b), (c:V {name:'C'}) RETURN a.name,b.name")).ShouldHaveSingleItem().ShouldBe(new object?[] { "A", "C" });
        var explain = await Rows(session, "EXPLAIN " + sources[2]);
        explain.Any(row => row[5] is string detail && detail.Contains("quantifier={1,3}", StringComparison.Ordinal)).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Quantified paths: 64 hops succeed and a qualifying 65th hop fails without partial rows")]
    public async Task Execute_HopCap_ShouldCheckOnlyEligibleRelationshipsAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("quantified", new());
        var database = await engine.CreateDatabaseAsync("graph");
        await using var session = await database.CreateSessionAsync();
        string nodes = string.Concat(Enumerable.Range(0, 64).Select(i => (i == 0 ? "(n:V {k:0})" : "") + "-[:T]->(:V {k:" + (i + 1) + "})"));
        await session.ExecuteAsync("INSERT " + nodes);
        // Act / Assert
        (await Rows(session, "MATCH (a:V {k:0})-[:T]->{64}(b) RETURN b.k")).ShouldHaveSingleItem()[0].ShouldBe(64L);
        (await Rows(session, "MATCH (a:V {k:0})-[:T]->+(b) RETURN count(*)")).ShouldHaveSingleItem()[0].ShouldBe(64L);
        await session.ExecuteAsync("MATCH (a:V {k:64}) INSERT (a)-[:Other]->(:V {k:65})");
        (await Rows(session, "MATCH (a:V {k:0})-[:T]->+(b) RETURN count(*)")).ShouldHaveSingleItem()[0].ShouldBe(64L);
        await session.ExecuteAsync("MATCH (a:V {k:64}) INSERT (a)-[:T]->(:V {k:66})");
        foreach (string tail in new[] { "RETURN b.k", "WHERE b.k=-1 RETURN b.k", "RETURN b.k ORDER BY b.k LIMIT 1" })
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("MATCH (a:V {k:0})-[:T]->+(b) " + tail));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05004");
            error.Message.ShouldContain("64 relationships", Case.Sensitive);
        }
        // D4 permits an unordered LIMIT to leave later branches unvisited. A visited 65th hop
        // still fails above, including when ORDER BY needs the complete expansion.
        (await Rows(session, "MATCH (a:V {k:0})-[:T]->+(b) RETURN b.k LIMIT 1")).ShouldHaveSingleItem()[0].ShouldBe(1L);
        session.LastStatementMetrics!.Memory.ActiveLeases.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Quantified paths: both expansion and binding budgets apply to repeated segments")]
    [InlineData(18, "", "retained execution-memory budget", "CG05020")]
    [InlineData(32, " {k:-1}", "1000000 candidate expansions", "CG05004")]
    public async Task Execute_ExpansionBudgets_ShouldFailWithoutTruncatingAsync(int count, string endpoint, string message, string code)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var engine = GraphDatabaseEngine.Create("quantified", new());
        var database = await engine.CreateDatabaseAsync("graph", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await session.ExecuteAsync("FOR k IN [" + string.Join(',', Enumerable.Range(0, count)) + "] INSERT (:V {k:k})", cancellationToken: timeout.Token);
        await session.ExecuteAsync("MATCH (a:V),(b:V) INSERT (a)-[:T]->(b)", cancellationToken: timeout.Token);
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(GraphQueryRequest.FromGql("MATCH (a:V)-[:T]->{1,3}(b:V" + endpoint + ") RETURN a.k", null,
            new() { MemoryBytes = code == "CG05020" ? 65536 : null }), timeout.Token));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
        error.Message.ShouldContain(message, Case.Sensitive);
        session.LastStatementMetrics!.Memory.RetainedBytes.ShouldBe(0);
        session.LastStatementMetrics.Memory.ActiveLeases.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Quantified paths: every mode observes cancellation after expansion has started")]
    [InlineData("")]
    [InlineData("WALK")]
    [InlineData("TRAIL")]
    [InlineData("SIMPLE")]
    [InlineData("ACYCLIC")]
    public async Task MatchAsync_MidExpansionCancellation_ShouldObserveTokenAsync(string mode)
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        await using var engine = GraphDatabaseEngine.Create("quantified", new());
        var database = await engine.CreateDatabaseAsync("graph");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync(chain);
        var budget = new GraphPlanExecutor.ExpansionBudget { OnConsume = count => { if (count == 4) { cancellation.Cancel(); } } };
        // Act
        await database.RunAsync(session, async operation =>
        {
            var plan = new GraphPlanner(database, operation.Context.Snapshot).Plan(GraphQueryRequest.FromGql("MATCH " + mode + " (a:V {name:'A'})-[:T]->{1,3}(b) RETURN b").Statement.GqlExpression);
            await Should.ThrowAsync<OperationCanceledException>(async () => await GraphPlanExecutor.MatchAsync(database, operation,
                plan.MatchClauses[0].Paths[0], new(StringComparer.Ordinal), [], budget, null, cancellation.Token));
            return true;
        }, CancellationToken.None);
        // Assert
        budget.Consumed.ShouldBe(4);
        budget.Memory.RetainedBytes.ShouldBe(0);
        budget.Memory.ActiveLeases.ShouldBe(0);
        cancellation.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Quantified paths: unbounded default TRAIL observes cancellation in the expansion dispatcher (#1733)")]
    public async Task MatchAsync_UnboundedTrailCancellation_ShouldStopBeforeNextFrontierAsync()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        await using var engine = GraphDatabaseEngine.Create("quantified", new());
        var database = await engine.CreateDatabaseAsync("graph");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("FOR k IN [" + string.Join(',', Enumerable.Range(0, 12)) + "] INSERT (:V {k:k})");
        await session.ExecuteAsync("MATCH (a:V),(b:V) INSERT (a)-[:T]->(b)");
        int frontierVisits = 0;
        int visitsAtCancellation = 0;
        GraphPlanExecutor.ExpansionBudget? budget = null;
        budget = new()
        {
            OnFrontier = _ =>
            {
                // A successful quantified hop queues EndIterationTask. Cancel before that
                // continuation is dispatched: without its own check it would queue RepeatTask
                // and reach this hook again, before any storage/materialization token check.
                cancellation.IsCancellationRequested.ShouldBeFalse();
                frontierVisits++;
                if (budget!.Viable == 32)
                {
                    visitsAtCancellation = frontierVisits;
                    cancellation.Cancel();
                }
            },
        };

        // Act
        await database.RunAsync(session, async operation =>
        {
            var plan = new GraphPlanner(database, operation.Context.Snapshot).Plan(
                GraphQueryRequest.FromGql("MATCH (a:V {k:0})-[:T]->+(b) RETURN b").Statement.GqlExpression);
            plan.MatchClauses[0].Paths[0].Mode.ShouldBe(GqlPathMode.Trail);
            var error = await Should.ThrowAsync<OperationCanceledException>(async () =>
                await GraphPlanExecutor.MatchAsync(database, operation, plan.MatchClauses[0].Paths[0],
                    new(StringComparer.Ordinal), [], budget, null, cancellation.Token));
            error.CancellationToken.ShouldBe(cancellation.Token);
            return true;
        }, CancellationToken.None);

        // Assert
        cancellation.IsCancellationRequested.ShouldBeTrue();
        visitsAtCancellation.ShouldBeGreaterThan(0);
        frontierVisits.ShouldBe(visitsAtCancellation);
        budget.Viable.ShouldBe(32);
        budget.Consumed.ShouldBeGreaterThan(32);
        budget.Memory.RetainedBytes.ShouldBe(0);
        budget.Memory.ActiveLeases.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Quantified paths: malformed and deeply nested direct ASTs fail before execution")]
    public async Task Plan_InvalidAst_ShouldReportShapeDiagnosticAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("quantified", new());
        var database = await engine.CreateDatabaseAsync("graph");
        await using var session = await database.CreateSessionAsync();
        var empty = new Dictionary<string, GqlExpression>();
        var a = new GqlNodePattern("a", [], empty);
        var b = new GqlNodePattern("b", [], empty);
        var edge = new GqlRelationshipPattern(null, "T", GqlPatternDirection.Outgoing, empty);
        GqlPathPattern Path(GqlQuantifier quantifier) => new([a, b], [edge with { Quantifier = quantifier }]);
        var inner = Path(new GqlQuantifier(1, 2));
        var nested = new GqlPathPattern([], []) { Elements = [new GqlParenthesizedPathPattern(inner, new GqlQuantifier(2, 2))] };
        var deep = new GqlPathPattern([a, b], [edge]);
        for (int i = 0; i < 100_000; i++) { deep = new GqlPathPattern([], []) { Elements = [new GqlParenthesizedPathPattern(deep)] }; }
        var reused = new GqlPathPattern([a], []);
        for (int i = 0; i < 40; i++)
        { reused = new GqlPathPattern([], []) { Elements = [new GqlParenthesizedPathPattern(reused), new GqlParenthesizedPathPattern(reused)] }; }
        var bad = new[] { Path(new(-1, 1)), Path(new(3, 1)), Path(new(1, 65)), Path(new(65, null)), nested, deep, reused,
            new GqlPathPattern([a,b,a], [edge with { Quantifier = new(1,40) }, edge with { Quantifier = new(1,40) }]),
            new GqlPathPattern([a, b], [edge]) { Elements = [new GqlPathNode(a)] },
            new GqlPathPattern([], []) { Elements = [new GqlParenthesizedPathPattern(new GqlPathPattern([a,b], [edge]), new GqlQuantifier(0,0))] } };
        // Act / Assert
        foreach (var path in bad)
        {
            var query = new GqlQueryStatement(new GqlQueryExpression([path], null, [], [], false, [new GqlProjection(new GqlVariableExpression("a"))]));
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(new GraphQueryRequest(query)));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05001");
        }
        var insertion = new GqlQueryStatement(new GqlQueryExpression([], null, [inner], [], false, []));
        (await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(new GraphQueryRequest(insertion))))
            .Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05001");
        (await Rows(session, "MATCH (a) RETURN count(*)")).ShouldHaveSingleItem()[0].ShouldBe(0L);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Quantified paths: incoming and either-direction segments execute stored edges correctly")]
    [InlineData("<-[:T]-{1,3}", "D", "C,B,A")]
    [InlineData("-[:T]-{1,3}", "D", "C,B,A")]
    [InlineData("<-[:T]->{1,3}", "D", "C,B,A")]
    public async Task Execute_DirectedQuantifiers_ShouldRespectTraversalAsync(string segment, string start, string expected)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("quantified", new());
        var database = await engine.CreateDatabaseAsync("graph");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync(chain);
        // Act
        var rows = await Rows(session, "MATCH (a:V {name:'" + start + "'})" + segment + "(b) RETURN b.name");
        // Assert
        rows.Select(row => row[0]).ShouldBe(expected.Split(',').Cast<object?>(), ignoreOrder: true);
        var paths = await PathKeys(session, "MATCH p = (a:V {name:'" + start + "'})" + segment + "(b) RETURN p");
        paths.Length.ShouldBe(3);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Quantified paths: hop properties and bound relationship identity precede the hop cap")]
    public async Task Execute_FilteredHopCap_ShouldRejectOnlyPassingEdgesAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("quantified", new());
        var database = await engine.CreateDatabaseAsync("graph");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("INSERT (a:V {k:0})" + string.Concat(Enumerable.Range(1, 64).Select(i => "-[:T {w:1}]->(:V {k:" + i + "})")));
        await session.ExecuteAsync("MATCH (a:V {k:64}) INSERT (a)-[:T {w:2}]->(:V {k:65}), (:Other)-[:Other]->(:Other)");
        // Act / Assert
        (await Rows(session, "MATCH (a:V {k:0})-[:T {w:1}]->+(b) RETURN count(*)")).ShouldHaveSingleItem()[0].ShouldBe(64L);
        (await Rows(session, "MATCH ()-[r:Other]->() WITH r MATCH (a:V {k:0})-[:T {w:1}]->+(b)-[r]->(c) RETURN count(*)"))
            .ShouldHaveSingleItem()[0].ShouldBe(0L);
        await session.ExecuteAsync("MATCH ()-[r:T {w:2}]->() DELETE r");
        await session.ExecuteAsync("MATCH (a:V {k:64}) INSERT (a)-[:T {w:1}]->(:V {k:66})");
        foreach (string map in new[] { "{w:1}", "{w:c.k}" })
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
                "MATCH (a:V {k:0})-[:T " + map + "]->+(b), (c:V {k:1}) RETURN count(*)"));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05004");
        }
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Quantified paths: parsed text can nest subpaths beyond the direct AST cap")]
    public async Task Execute_DeepParsedSubpaths_ShouldPlanAndRunAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("quantified", new());
        var database = await engine.CreateDatabaseAsync("graph");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync(chain);
        string source = "MATCH " + new string('(', 129) + "(a:V {name:'A'})->(b)" + new string(')', 129) + " RETURN b.name";
        // Act / Assert
        (await Rows(session, "EXPLAIN " + source)).ShouldNotBeEmpty();
        (await Rows(session, source)).ShouldHaveSingleItem()[0].ShouldBe("B");
    }

    private static async Task<string[]> PathKeys(GraphDatabaseSession session, string source)
    {
        var result = (GraphPathsQueryResult)await session.ExecuteAsync(GraphPathsQueryRequest.FromGql(source));
        foreach (var path in result.Paths)
        {
            for (int i = 0; i < path.Relationships.Count; i++)
            {
                var edge = path.Relationships[i];
                ((edge.From == path.Nodes[i].Id && edge.To == path.Nodes[i + 1].Id) ||
                    (edge.To == path.Nodes[i].Id && edge.From == path.Nodes[i + 1].Id)).ShouldBeTrue();
            }
        }
        return result.Paths.Select(path => string.Join(',', path.Nodes.Select(node => node.Id)) + "/" + string.Join(',', path.Relationships.Select(edge => edge.Id))).ToArray();
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
