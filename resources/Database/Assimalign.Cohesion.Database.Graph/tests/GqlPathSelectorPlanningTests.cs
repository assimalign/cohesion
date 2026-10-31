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
using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GqlPathSelectorPlanningTests
{
    private readonly ITestOutputHelper _output;

    public GqlPathSelectorPlanningTests(ITestOutputHelper output) => _output = output;

    [Theory(DisplayName = "Cohesion Test [Graph] - Selected prefilters refuse dependencies on peer MATCH paths (#1734)")]
    [InlineData("ANY ((a) WHERE b.k > 0), (b)", "b.k")]
    [InlineData("(b), ALL SHORTEST (a WHERE b.k > 0)", "b.k")]
    [InlineData("ANY (a)-[r WHERE element_id(b) <> 'x']->(c), (b)", "b)")]
    [InlineData("ANY (a) ((x WHERE b.k > 0)->(y)){0,3} (c), (b)", "b.k")]
    [InlineData("p = ANY ((a) WHERE path_length(q) > 0), q = (b)", "q)")]
    [InlineData("ANY (a {k:b.k}), (b)", "b.k")]
    [InlineData("(a WHERE b.k>0), (b)", "b.k")]
    [InlineData("((a) WHERE b.k>0), (b)", "b.k")]
    [InlineData("ANY (a)-[r {k:b.k}]->(c), (b)", "b.k")]
    public async Task Plan_PeerPrefilter_ShouldRefuseAtReferenceAsync(string pattern, string reference)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("selectors", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        string source = $"EXPLAIN MATCH {pattern} RETURN a";
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync(source, cancellationToken: CancellationToken.None));
        // Assert
        var diagnostic = error.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("CG01016");
        diagnostic.Start.ShouldBe(source.IndexOf(reference, StringComparison.Ordinal));
        diagnostic.End.ShouldNotBeNull().ShouldBeGreaterThan(diagnostic.Start!.Value);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Selected prefilters retain their own and incoming bindings (#1734)")]
    [InlineData("MATCH (b) MATCH ANY ((a) WHERE b.k > 0), (b) RETURN a")]
    [InlineData("MATCH ANY ((a)->(b) WHERE b.k > 0), (c) RETURN a")]
    [InlineData("MATCH p = ANY ((a) WHERE path_length(p) > 0) RETURN a")]
    [InlineData("MATCH ANY (a) ((x WHERE x.k > 0)->(y)){0,3} (b) RETURN a")]
    public async Task Plan_LocalPrefilter_ShouldRetainAllowedBindingsAsync(string source)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("selectors", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var plan = await PlanAsync(database, session, source);
        // Assert
        plan.MatchClauses.SelectMany(clause => clause.Paths).ShouldContain(path => path.Selection != null);
    }

#if !DEBUG
    [Theory(DisplayName = "Cohesion Test [Graph] - Ordinary planning stays within base allocation budgets (#1734)")]
    [InlineData("MATCH (a) RETURN a.k", 11256L, 10672L)]
    [InlineData("MATCH (a)->(b) RETURN a.k", 12208L, 11544L)]
    [InlineData("MATCH (a) ((x)->(y)){0,3} (b) RETURN a.k", 15168L, 14424L)]
    public async Task Plan_UnselectedPaths_ShouldKeepBaseAllocationBudgetAsync(string source, long budget, long isolatedBase)
    {
        // Arrange: parse and acquire the snapshot outside the synchronous measured loop.
        await using var engine = GraphDatabaseEngine.Create("selectors", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var parsed = GraphQueryRequest.FromGql(source).Statement.GqlExpression;
        // Measure ordinary planning with a known token. Unknown names now intentionally
        // allocate CG01023 diagnostics; that path has its own warning coverage (#1249).
        await session.ExecuteAsync("INSERT (:Probe {k:1})", cancellationToken: CancellationToken.None);
        var statement = new GqlQueryExpression(parsed.MatchClauses, [], [], false, parsed.Projections);
        long bytes = await database.RunAsync(session, operation =>
        {
            GraphPlan Plan() => new GraphPlanner(database, operation.Context.Snapshot).Plan(statement, token: CancellationToken.None);
            for (int i = 0; i < 100; i++) { GC.KeepAlive(Plan()); }
            long minimum = long.MaxValue;
            // Act: minimum of five 100-plan rounds; no await can move the allocation counter.
            for (int round = 0; round < 5; round++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 100; i++) { GC.KeepAlive(Plan()); }
                minimum = Math.Min(minimum, GC.GetAllocatedBytesForCurrentThread() - before);
            }
            return ValueTask.FromResult(minimum / 100);
        }, CancellationToken.None);
        // Assert
        _output.WriteLine($"{source}: {bytes} bytes/plan; review base budget={budget}; isolated base={isolatedBase}");
        bytes.ShouldBeLessThanOrEqualTo(budget);
        bytes.ShouldBeLessThanOrEqualTo(isolatedBase);
    }
#endif

    [Theory(DisplayName = "Cohesion Test [Graph] - Selector plans distinguish path counts and distinct length groups (#1734)")]
    [InlineData("ANY", 1L, false, false)]
    [InlineData("ALL", null, false, false)]
    [InlineData("ANY SHORTEST", 1L, false, true)]
    [InlineData("ALL SHORTEST", 1L, true, true)]
    [InlineData("SHORTEST 2", 2L, false, true)]
    [InlineData("SHORTEST 2 GROUPS", 2L, true, true)]
    [InlineData("SHORTEST 1 GROUPS", 1L, true, true)]
    public async Task Plan_SelectorCorpus_ShouldRetainPartitionAndCountUnitAsync(string prefix, long? count, bool groups, bool ordered)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("selectors", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var plan = await PlanAsync(database, session, $"MATCH p = {prefix} (a)->{{0,3}}(b) RETURN path_length(p)");
        // Assert
        var path = plan.MatchClauses.ShouldHaveSingleItem().Paths.ShouldHaveSingleItem();
        var selection = path.Selection.ShouldNotBeNull();
        selection.Count.ShouldBe(count);
        selection.CountLengthGroups.ShouldBe(groups);
        selection.OrderByLength.ShouldBe(ordered);
        selection.Mode.ShouldBe(GqlPathMode.Trail);
        selection.Search.ShouldBeSameAs(path.Shape);
        selection.StartVariables.ShouldBe(["a"]);
        selection.EndVariables.ShouldBe(["b"]);
        selection.BoundEndpoints.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Every mode plans cycles, bound endpoints and quantified zero paths (#1734)")]
    [InlineData("WALK")]
    [InlineData("TRAIL")]
    [InlineData("SIMPLE")]
    [InlineData("ACYCLIC")]
    public async Task Plan_ModeIntegration_ShouldKeepEndpointPartitionAsync(string mode)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("selectors", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var plan = await PlanAsync(database, session, $"MATCH (a), (b) MATCH p = ALL SHORTEST {mode} (a) ((x)->(y)){{0,3}} (b), q = ANY (a)->*(a) RETURN path_length(p)");
        // Assert
        var paths = plan.MatchClauses[1].Paths;
        paths[0].Selection.ShouldNotBeNull().BoundEndpoints.ShouldBe(["a", "b"]);
        paths[0].Selection!.Mode.ShouldBe(Enum.Parse<GqlPathMode>(mode, true));
        paths[0].Shape!.HasQuantifier.ShouldBeTrue();
        paths[1].Selection!.StartVariables.ShouldBe(["a"]);
        paths[1].Selection!.EndVariables.ShouldBe(["a"]);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Post-selection filters cannot become index search constraints or empty-plan shortcuts (#1734)")]
    public async Task Plan_Postfilter_ShouldRemainOutsideSearchAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("selectors", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:V {k:0})-[:T]->(:V {k:1})", cancellationToken: CancellationToken.None);
        await GraphSchema.Open(database, session).CreateIndexAsync("V", "by_k", "k", CancellationToken.None);
        // Act
        var ordinary = await PlanAsync(database, session, "MATCH (a:V)->(b) WHERE a.k=0 RETURN a.k");
        var selective = await PlanAsync(database, session, "MATCH ANY SHORTEST (a:V)->(b) WHERE a.k=0 RETURN a.k");
        var unknown = await PlanAsync(database, session, "MATCH ANY SHORTEST (a:V)->(b) WHERE a:Missing RETURN a.k");
        // Assert
        ordinary.MatchClauses[0].Paths[0].Anchor.Property.ShouldBe("k");
        selective.MatchClauses[0].Paths[0].Anchor.Property.ShouldBeNull();
        selective.MatchClauses[0].Predicate.ShouldNotBeNull();
        unknown.MatchesNothing.ShouldBeFalse();
        unknown.MatchClauses[0].Paths[0].Anchor.Label.ShouldBe("V");
        unknown.Warnings.ShouldContain(warning => warning.Code == "CG01010");
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Path prefilters retain flattened boundaries and repeated singleton scope (#1734)")]
    public async Task Plan_Prefilters_ShouldRetainSearchScopeAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("selectors", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var plan = await PlanAsync(database, session, "MATCH ANY SHORTEST (a)->(b) ((x WHERE x.k > 0)-[r WHERE r.w > 0]->(y) WHERE x.k <> y.k) RETURN a");
        var repeated = await PlanAsync(database, session, "MATCH ANY SHORTEST (a) ((x WHERE x.k > 0)-[r WHERE r.w > 0]->(y) WHERE x.k <> y.k){0,3} (b) RETURN size(r)");
        // Assert
        var shape = plan.MatchClauses[0].Paths[0].Selection!.Search;
        shape.ElementPrefilterCount.ShouldBe(2);
        shape.PathPrefilterCount.ShouldBe(1);
        shape.Prefilters.ShouldHaveSingleItem().StartBoundary.ShouldBe(1);
        shape.Prefilters[0].EndBoundary.ShouldBe(2);
        repeated.MatchClauses[0].Paths[0].Shape!.PathPrefilterCount.ShouldBe(1);
        var outside = await Should.ThrowAsync<DatabaseException>(async () => await PlanAsync(database, session,
            "MATCH ANY SHORTEST (a) ((x)-[r]->(y) WHERE r.w > 0){0,3} (b) WHERE r.w > 0 RETURN a"));
        outside.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG03003");
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Sibling and nested prefilters retain their individual boundary offsets (#1734)")]
    public void Create_AdjacentFilteredGroups_ShouldRetainBoundaries()
    {
        // Arrange
        var path = GraphQueryRequest.FromGql("MATCH ANY ((a) WHERE a.k>0) ((b)->(c) WHERE b.k>1) (((d)->(e) WHERE d.k>2) WHERE e.k>3) RETURN a")
            .Statement.GqlExpression.Matches[0];
        // Act
        var shape = GraphPathShape.Create(path);
        // Assert
        shape.Prefilters.Select(filter => (filter.StartBoundary, filter.EndBoundary))
            .ShouldBe([(0, 0), (0, 1), (1, 2), (1, 2)]);
        shape.Nodes.Select(nodes => nodes.Select(node => node.Variable).ToArray()).ShouldBe(new string?[][] { ["a", "b"], ["c", "d"], ["e"] });
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Deep path prefilters lower with linear retained planner allocations (#1734)")]
    [InlineData(false)]
    [InlineData(true)]
    public void Create_DeepPrefilters_ShouldAllocateLinearly(bool precedingSibling)
    {
        // Arrange: isolate shape lowering from parser-stack size and AST construction.
        var small = Nested(512, precedingSibling);
        var large = Nested(1024, precedingSibling);
        var medium = Nested(256, precedingSibling);
        _ = GraphPathShape.Create(Nested(32, precedingSibling), parsed: true);
        // Act: use the minimum of three rounds after warming the lowering path.
        long smallBytes = Enumerable.Range(0, 3).Min(_ => Measure(small, 512));
        long largeBytes = Enumerable.Range(0, 3).Min(_ => Measure(large, 1024));
        long mediumBytes = Enumerable.Range(0, 3).Min(_ => Measure(medium, 256));
        _output.WriteLine($"prefilters precedingSibling={precedingSibling}: 256={mediumBytes}; 512={smallBytes}; 1024={largeBytes}");
        // Assert: doubling nesting must not quadruple filter storage.
        ((double)largeBytes / smallBytes).ShouldBeLessThan(2.6, $"small={smallBytes}; large={largeBytes}");
        ((double)smallBytes / mediumBytes).ShouldBeLessThan(2.6, $"medium={mediumBytes}; small={smallBytes}");

        static GqlPathPattern Nested(int count, bool sibling)
        {
            var node = new GqlNodePattern(null, [], new Dictionary<string, GqlExpression>());
            var path = new GqlPathPattern([node], []);
            for (int i = 0; i < count; i++)
            {
                var filter = new GqlLiteralExpression(true);
                path = sibling
                    ? new GqlPathPattern([], []) { Elements = [new GqlParenthesizedPathPattern(new GqlPathPattern([node], []) { Predicate = filter }), new GqlParenthesizedPathPattern(path)] }
                    : new GqlPathPattern([], []) { Elements = [new GqlParenthesizedPathPattern(path)], Predicate = filter };
            }
            return path;
        }
        static long Measure(GqlPathPattern path, int count)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            var shape = GraphPathShape.Create(path, parsed: true);
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            shape.Prefilters.Count.ShouldBe(count);
            GC.KeepAlive(shape);
            return bytes;
        }
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Prefilters bind parameters and names before the execution refusal (#1734)")]
    [InlineData("MATCH ANY (a WHERE a.k=$missing) RETURN a", "CG05001")]
    [InlineData("MATCH ANY (a)-[r WHERE r.k=$missing]->(b) RETURN a", "CG05001")]
    [InlineData("MATCH ANY ((a)->(b) WHERE absent.k=0) RETURN a", "CG05001")]
    [InlineData("MATCH ANY ((a)->(b) WHERE sum(a.k)>0) RETURN a", "CG05001")]
    public async Task Plan_InvalidPrefilter_ShouldFailBindingAsync(string source, string code)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("selectors", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("EXPLAIN " + source, cancellationToken: CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - EXPLAIN exposes selectors, prefilters, postfilters and separate retained-state budgeting (#1734)")]
    public async Task ExecuteAsync_Explain_ShouldDescribeSelectionWithoutExpandingAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("selectors", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        const string source = "EXPLAIN MATCH p = SHORTEST 2 WALK GROUPS ((a WHERE a.k>0)-[r WHERE r.w>0]->*(b) WHERE path_length(p)>0) WHERE b.k=3 RETURN path_length(p)";
        // Act
        await using var result = (DatabasePlanDescription)await session.ExecuteAsync(source, cancellationToken: CancellationToken.None);
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        // Assert
        var selector = rows.Single(row => Equals(row[2], "SelectPaths"));
        selector[5].ShouldBeOfType<string>().ShouldContain("partition=endpoint_pair", Case.Sensitive);
        selector[5].ShouldBeOfType<string>().ShouldContain("count=2; count_unit=distinct_lengths; order=length,identity", Case.Sensitive);
        selector[5].ShouldBeOfType<string>().ShouldContain("mode=Walk", Case.Sensitive);
        var search = rows.Single(row => Equals(row[2], "PathSearch"));
        search[1].ShouldBe(selector[0]);
        search[5].ShouldBeOfType<string>().ShouldContain("state_and_output_memory_limit=268435456", Case.Sensitive);
        rows.Single(row => Equals(row[2], "PathPrefilter"))[1].ShouldBe(search[0]);
        rows.Where(row => Equals(row[2], "ElementPrefilter")).ShouldAllBe(row => Equals(row[1], search[0]));
        selector[1].ShouldBe(rows.Single(row => Equals(row[2], "PostFilter"))[0]);
        rows.ShouldAllBe(row => row[7] == null);
        session.LastStatementMetrics!.Physical.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Planned selectors and prefilters execute on empty input (#1734)")]
    [InlineData("MATCH ANY (a) RETURN a.k")]
    [InlineData("MATCH ALL (a) RETURN a.k LIMIT 0")]
    [InlineData("MATCH ANY SHORTEST WALK (a)->*(b) RETURN a.k")]
    [InlineData("OPTIONAL MATCH ALL SHORTEST (a)->(b) RETURN a.k")]
    [InlineData("MATCH ALL SHORTEST (a)->(b) DETACH DELETE b")]
    [InlineData("INSERT (:Pending) MATCH SHORTEST 2 (a)->(b) RETURN a.k")]
    [InlineData("MATCH SHORTEST 2 GROUPS (a)-[*0]->(b) RETURN a.k")]
    [InlineData("MATCH ((a)->(b) WHERE a.k>0) RETURN a.k")]
    public async Task ExecuteAsync_PlannedSelector_ShouldExecuteAsync(string source)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("selectors", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var result = await session.ExecuteAsync(source, cancellationToken: CancellationToken.None);
        if (result is DatabaseResultSet rows) { await rows.DisposeAsync(); }
        // Assert
        session.LastStatementMetrics!.Memory.RetainedBytes.ShouldBe(0);
        session.LastStatementMetrics.Memory.ActiveLeases.ShouldBe(0);
        await using var count = (DatabaseResultSet)await session.ExecuteAsync("MATCH (n) RETURN count(*)", cancellationToken: CancellationToken.None);
        await foreach (var row in count.GetRowsAsync(CancellationToken.None))
        { row.GetValue(0).ShouldBe(source.StartsWith("INSERT", StringComparison.Ordinal) ? 1L : 0L); }
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Source-free selector trees have an independent semantic gate (#1734)")]
    public void Create_InvalidSelectorAst_ShouldRefuseMalformedTrees()
    {
        // Arrange
        var node = new GqlNodePattern("a", [], new Dictionary<string, GqlExpression>());
        var path = new GqlPathPattern([node], []);
        // Act / Assert
        foreach (var selector in new[] { new GqlPathSelector((GqlPathSelectorKind)255),
            new(GqlPathSelectorKind.Shortest, 0), new(GqlPathSelectorKind.Any, 2) })
        { Should.Throw<GraphPlanningException>(() => GraphPathShape.Create(path with { Selector = selector })).Diagnostics[0].Code.ShouldBe("CG05001"); }
        var selected = path with { Selector = new(GqlPathSelectorKind.Any) };
        Should.Throw<GraphPlanningException>(() => GraphPathShape.Create(selected, creating: true)).Diagnostics[0].Code.ShouldBe("CG05001");
        var nested = new GqlPathPattern([], []) { Elements = [new GqlParenthesizedPathPattern(selected)] };
        Should.Throw<GraphPlanningException>(() => GraphPathShape.Create(nested)).Diagnostics[0].Code.ShouldBe("CK00001");
        GraphPathShape.Create(path with { Mode = GqlPathMode.Walk, Selector = new(GqlPathSelectorKind.AnyShortest) }).ShouldNotBeNull();
    }

    private static async Task<GraphPlan> PlanAsync(GraphDatabase database, GraphDatabaseSession session, string source)
        => await database.RunAsync(session, operation => ValueTask.FromResult(new GraphPlanner(database, operation.Context.Snapshot)
            .Plan(GraphQueryRequest.FromGql(source).Statement.GqlExpression, token: CancellationToken.None)), CancellationToken.None);
}
