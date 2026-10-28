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

public sealed class GraphReduceTests
{
    [Theory(DisplayName = "Cohesion Test [Graph] - List predicates: NULLs and decisive values follow the reference truth tables")]
    [InlineData("NULL", null, null, null, null)]
    [InlineData("[]", true, false, true, false)]
    [InlineData("[TRUE]", true, true, false, true)]
    [InlineData("[FALSE]", false, false, true, false)]
    [InlineData("[NULL]", null, null, null, null)]
    [InlineData("[TRUE,TRUE]", true, true, false, false)]
    [InlineData("[FALSE,FALSE]", false, false, true, false)]
    [InlineData("[TRUE,FALSE]", false, true, false, true)]
    [InlineData("[TRUE,NULL]", null, true, false, null)]
    [InlineData("[NULL,TRUE]", null, true, false, null)]
    [InlineData("[FALSE,NULL]", false, null, null, null)]
    [InlineData("[NULL,FALSE]", false, null, null, null)]
    [InlineData("[NULL,NULL]", null, null, null, null)]
    [InlineData("[NULL,TRUE,TRUE]", null, true, false, false)]
    [InlineData("[TRUE,TRUE,NULL]", null, true, false, false)]
    [InlineData("[FALSE,NULL,TRUE]", false, true, false, null)]
    public async Task ExecuteAsync_ListPredicate_ShouldMatchThreeValuedLogicAsync(string list, bool? all, bool? any, bool? none, bool? single)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("predicate-truth", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var values = await RowAsync(session, $"RETURN all(x IN {list} WHERE x), any(x IN {list} WHERE x), none(x IN {list} WHERE x), single(x IN {list} WHERE x)");
        // Assert
        values.ShouldBe(new object?[] { all, any, none, single });
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Reduce: ordered evaluation, empty/NULL sources and dynamic accumulator types")]
    [InlineData("reduce(a=0,x IN [1,2,3] | a+x)", 6L)]
    [InlineData("reduce(a=TRUE,x IN [TRUE,TRUE] | a AND x)", true)]
    [InlineData("reduce(a=TRUE,x IN [TRUE,FALSE] | a AND x)", false)]
    [InlineData("reduce(a=TRUE,x IN [TRUE,NULL] | a AND x)", null)]
    [InlineData("reduce(a=TRUE,x IN [FALSE,NULL] | a AND x)", false)]
    [InlineData("reduce(a=0,A IN [1,2] | a+A)", 3L)]
    [InlineData("reduce(a=20,x IN [1,2,3] | a-x)", 14L)]
    [InlineData("reduce(a=0,x IN [1,2,3] | a*10+x)", 123L)]
    [InlineData("reduce(a=42,x IN [] | 1/0)", 42L)]
    [InlineData("reduce(a=1/0,x IN NULL | a)", null)]
    [InlineData("reduce(a=NULL,x IN [] | x)", null)]
    [InlineData("reduce(a=NULL,x IN [1] | x)", 1L)]
    [InlineData("reduce(a=0,x IN [1,NULL,3] | a+x)", null)]
    [InlineData("reduce(a=0,x IN [1,NULL,3] | a+coalesce(x,0))", 4L)]
    [InlineData("reduce(a=0,x IN [1,2] | toString(x))", "2")]
    [InlineData("reduce(a=FALSE,x IN [1,2] | x>1)", true)]
    [InlineData("reduce(a=0,x IN [1,2] | CASE x WHEN 1 THEN 'changed' ELSE toString(a)||'!' END)", "changed!")]
    [InlineData("reduce(a=0,x IN [1,2] | CASE x WHEN 1 THEN {k:10} ELSE a.k+1 END)", 11L)]
    [InlineData("reduce(a=[],x IN [1,2] | [a,x])[1]", 2L)]
    public async Task ExecuteAsync_Reduce_ShouldPreserveOrderedAccumulatorAsync(string expression, object? expected)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("reduce-values", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act / Assert
        (await RowAsync(session, "RETURN " + expression)).ShouldBe(new[] { expected });
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - List predicates: evaluation stops only at a decisive result")]
    [InlineData("all", "[FALSE,TRUE]", false, 1)]
    [InlineData("any", "[TRUE,FALSE]", true, 1)]
    [InlineData("none", "[TRUE,FALSE]", false, 1)]
    [InlineData("single", "[TRUE,TRUE,FALSE]", false, 2)]
    [InlineData("single", "[FALSE,TRUE,FALSE]", true, 3)]
    [InlineData("all", "[NULL,FALSE,TRUE]", false, 2)]
    [InlineData("any", "[NULL,TRUE,FALSE]", true, 2)]
    [InlineData("none", "[NULL,TRUE,FALSE]", false, 2)]
    [InlineData("single", "[NULL,TRUE,TRUE,FALSE]", false, 3)]
    public async Task ExecuteAsync_PredicateShortCircuit_ShouldVisitOnlyRequiredElementsAsync(string name, string list, bool expected, int visits)
    {
        // Arrange
        int calls = 0;
        var builder = GraphDatabaseEngine.CreateBuilder("predicate-visits");
        builder.Functions.Add(GraphScalarFunction.Create<bool?, bool?>("observe", value => { calls++; return value; }, GraphNullBehavior.CalledOnNullInput));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        (await RowAsync(session, $"RETURN {name}(x IN {list} WHERE observe(x))")).ShouldBe(new object?[] { expected });
        // Assert
        calls.ShouldBe(visits);
        session.LastStatementMetrics!.Physical.ShouldBe(visits);
        session.LastStatementMetrics.Memory.ActiveLeases.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Reduce: source, seed and steps run once in reference order; EXPLAIN invokes none")]
    public async Task ExecuteAsync_ReductionCallbacks_ShouldPreserveOrderAsync()
    {
        // Arrange
        var calls = new List<long>();
        var builder = GraphDatabaseEngine.CreateBuilder("reduce-order");
        builder.Functions.Add(GraphScalarFunction.Create<long, long>("observe", value => { calls.Add(value); return value; }));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        const string query = "RETURN reduce(a=observe(0),x IN [observe(1),observe(2),observe(3)] | observe(a*10+x))";
        // Act / Assert
        await using (var plan = (DatabasePlanDescription)await session.ExecuteAsync("EXPLAIN " + query, cancellationToken: CancellationToken.None)) { calls.ShouldBeEmpty(); }
        (await RowAsync(session, query)).ShouldBe(new object?[] { 123L });
        calls.ShouldBe(new long[] { 1, 2, 3, 0, 1, 12, 123 });
        calls.Clear();
        (await RowAsync(session, "RETURN reduce(a=observe(0),x IN NULL | observe(1))")).ShouldBe(new object?[] { null });
        calls.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Binding forms: static invalid tests and sources fail under EXPLAIN and empty inputs")]
    [InlineData("all(x IN [] WHERE 1)", "CG02013")]
    [InlineData("any(x IN NULL WHERE 'x')", "CG02013")]
    [InlineData("none(x IN [] WHERE [])", "CG02013")]
    [InlineData("single(x IN NULL WHERE size([]))", "CG02013")]
    [InlineData("reduce(a=0,x IN 1 | a)", "CG02015")]
    [InlineData("any(x IN 'x' WHERE TRUE)", "CG02015")]
    [InlineData("none(x IN {} WHERE TRUE)", "CG02015")]
    [InlineData("single(x IN TRUE WHERE TRUE)", "CG02015")]
    [InlineData("all(x IN [] WHERE sum(x)>0)", "CG05025")]
    [InlineData("reduce(a=0,x IN [] | sum(x))", "CG05025")]
    [InlineData("reduce(x=0,x IN [] | x)", "CG01018")]
    [InlineData("all(x IN x WHERE TRUE)", "CG05001")]
    [InlineData("reduce(a=a,x IN [] | a)", "CG05001")]
    [InlineData("reduce(a=0,x IN a | a)", "CG05001")]
    [InlineData("reduce(a=0,x IN [] | absent)", "CG05001")]
    [InlineData("all(x IN NULL WHERE absent)", "CG05001")]
    [InlineData("reduce(a=0,x IN [] | a), a", "CG05001")]
    [InlineData("any(x IN [] WHERE TRUE), x", "CG05001")]
    public async Task ExecuteAsync_InvalidBindingForm_ShouldRefuseDuringPlanningAsync(string expression, string code)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("reduce-invalid", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act / Assert
        foreach (string prefix in new[] { "RETURN ", "EXPLAIN RETURN " })
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(prefix + expression, cancellationToken: CancellationToken.None));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
        }
        (await RowAsync(session, "RETURN 42")).ShouldBe(new object?[] { 42L });
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Lexical scopes: nested reductions and predicates shadow and restore both bindings")]
    public async Task ExecuteAsync_NestedBindings_ShouldRetainOuterAndGroupedValuesAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("reduce-scopes", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act / Assert
        foreach (var (query, expected) in new (string, object?)[]
        {
            ("RETURN ALL (1+2)", 3L),
            ("LET x=1 RETURN ALL (x IN [1])", true),
            ("FOR x IN [1,2] RETURN count(ALL (x IN [1]))", 2L),
            ("LET a=10, x=[1,2] RETURN reduce(a=a,x IN x | a+x)+a", 23L),
            ("LET a=99 RETURN reduce(a=0,x IN [1,2] | a+reduce(a=x,x IN [x] | a+x))+a", 105L),
            ("RETURN [x IN [1,2] | reduce(a=x,x IN [x+10] | a+x)][1]", 14L),
            ("RETURN reduce(a=0,x IN [1,2] | a+size([a IN [x] WHERE any(x IN [a] WHERE x>0) | a]))", 2L),
            ("FOR x IN [1,2,3] RETURN reduce(a=0,x IN collect(x) | a+x)", 6L),
            ("FOR x IN [1,2,3] RETURN reduce(a=sum(x),x IN [1,2] | a+x)", 9L),
            ("FOR x IN [1,2,3] RETURN all(x IN collect(x) WHERE x>0)", true),
            ("FOR x IN [1,2,3] RETURN sum(reduce(a=0,x IN [x] | a+x))", 6L),
            ("FOR x IN [1,2,3] RETURN count(all(y IN [x] WHERE y>1))", 3L),
            ("FOR x IN [1,1] RETURN x, count(*), reduce(a=x,x IN [2] | a+x) AS folded ORDER BY reduce(a=0,x IN [folded] | a+x)", 1L),
            ("LET x=99 RETURN all(x IN [1,2] WHERE x>0) AS x ORDER BY any(x IN [x] WHERE x)", true),
        })
        { (await RowAsync(session, query))[0].ShouldBe(expected, query); }
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Updates: reduce and predicates execute in properties, WHERE, SET and both MERGE branches")]
    public async Task ExecuteAsync_PropertyAndUpdateForms_ShouldUseLexicalValuesAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("reduce-updates", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        await session.ExecuteAsync("FOR k IN [1,2,1] MERGE (n:N {k:reduce(a=0,x IN [k] | a+x)}) ON CREATE SET n.c=reduce(a=10,x IN [k] | a+x),n.p=all(x IN [k] WHERE x>0) ON MATCH SET n.m=reduce(a=20,x IN [k] | a+x)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("MATCH (n:N {k:reduce(a=0,x IN [1] | a+x)}) WHERE single(x IN [n.k,2] WHERE x=1) SET n.m=reduce(a=n.m,x IN [1] | a+x)", cancellationToken: CancellationToken.None);
        // Assert
        (await RowAsync(session, "MATCH (n:N {k:1}) RETURN n.k,n.c,n.m,n.p")).ShouldBe(new object?[] { 1L, 11L, 22L, true });
        (await RowAsync(session, "MATCH (n:N {k:2}) RETURN n.k,n.c,n.m,n.p")).ShouldBe(new object?[] { 2L, 12L, null, true });
        (await RowAsync(session, "MATCH (n:N) WHERE none(x IN [n.k] WHERE x<0) RETURN sum(n.k)")).ShouldBe(new object?[] { 3L });
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Reduce shapes: scalar group projections cross Execute; entity and nested-entity accumulators stay gated")]
    public async Task ExecuteAsync_EntityAccumulator_ShouldPropagateConservativeShapesAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("reduce-shapes", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:N {k:1})-[:T {w:2}]->(:N {k:2})-[:T {w:3}]->(:N {k:3})", cancellationToken: CancellationToken.None);
        const string match = "MATCH (a:N {k:1})-[rs:T]->{2}(b) ";
        // Act / Assert
        (await RowAsync(session, match + "RETURN reduce(s=0,r IN rs | s+r.w), all(r IN rs WHERE r.w>0)")).ShouldBe(new object?[] { 5L, true });
        (await RowAsync(session, match + "WITH reduce(s=0,r IN rs | s+r.w) AS total RETURN total NEXT RETURN total")).ShouldBe(new object?[] { 5L });
        (await RowAsync(session, match + "WITH reduce(n=a,r IN rs | endNode(r)) AS n RETURN n.k")).ShouldBe(new object?[] { 3L });
        (await RowAsync(session, match + "RETURN (reduce(n=a,r IN rs | endNode(r))).k")).ShouldBe(new object?[] { 3L });
        (await RowAsync(session, match + "RETURN [n IN [reduce(n=a,r IN rs | endNode(r))] | n.k]")).ShouldBe(new object?[] { new object?[] { 3L } });
        foreach (string ordering in new[]
        {
            "all(x IN [x] WHERE x:N)",
            "[x IN [x] WHERE x:N | x.k][0]",
            "[x IN [x] | all(y IN [x] WHERE y:N)][0]",
            "(reduce(n=x,r IN [x] | r)).k",
            "all(x IN [1] WHERE all(y IN [projected] WHERE y:N))",
        })
        {
            var row = await RowAsync(session, "MATCH (a:N {k:1}) LET x=a RETURN a AS x,x AS projected ORDER BY " + ordering);
            row[0].ShouldBeOfType<GraphNode>().Properties["k"].ShouldBe(1L);
        }
        foreach (string value in new[]
        {
            "reduce(n=a,r IN rs | endNode(r))", "reduce(n=0,r IN rs | r)", "reduce(n=0,r IN [] | a)",
            "reduce(n=a,r IN [] | 0)", "reduce(n=[],r IN rs | [n,r])", "reduce(n={r:a},r IN rs | n)",
        })
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(new GraphQueryRequest(GraphQueryRequest.FromGql(match + "INSERT (:Unwritten) RETURN " + value).Statement) { ScalarsOnly = true }, CancellationToken.None));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05001");
        }
        (await RowAsync(session, "MATCH (n:Unwritten) RETURN count(*)")).ShouldBe(new object?[] { 0L });
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Reduce budgets: large and nested folds share limits, roll back and release all grants")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_ReduceLimit_ShouldRollbackAndReleaseAsync(bool memory)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("reduce-budgets", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var parameters = new Dictionary<string, object?> { ["xs"] = Enumerable.Range(0, 100).ToArray() };
        string value = memory ? "reduce(a=[],x IN $xs | [a,x])" : "reduce(a=0,x IN $xs | a+reduce(a=0,y IN $xs | a+y))";
        var request = GraphQueryRequest.FromGql("INSERT (:Unwritten) RETURN " + value, parameters, memory ? new() { MemoryBytes = 8192 } : new() { WorkUnits = 50 });
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(request, CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(memory ? "CG05020" : "CG05004");
        session.LastStatementMetrics!.Memory.RetainedBytes.ShouldBe(0);
        session.LastStatementMetrics.Memory.ActiveLeases.ShouldBe(0);
        (await RowAsync(session, "MATCH (n:Unwritten) RETURN count(*)")).ShouldBe(new object?[] { 0L });
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Reduce memory: replaced values are released during a long scalar fold")]
    public async Task ExecuteAsync_ReplacedAccumulator_ShouldReleasePreviousValueAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("reduce-release", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var request = GraphQueryRequest.FromGql("RETURN reduce(a='seed',x IN $xs | toString(x)||$tail)", new Dictionary<string, object?> { ["xs"] = Enumerable.Range(0, 1000).ToArray(), ["tail"] = new string('a', 1024) }, new() { MemoryBytes = 131072 });
        // Act
        await using var rows = (DatabaseResultSet)await session.ExecuteAsync(request, CancellationToken.None);
        // Assert
        await foreach (var row in rows.GetRowsAsync(CancellationToken.None)) { row.GetValue(0).ShouldBe("999" + new string('a', 1024)); }
        session.LastStatementMetrics!.Memory.RetainedBytes.ShouldBe(0);
        session.LastStatementMetrics.Memory.ActiveLeases.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Binding ASTs: malformed public shapes retain structured identities")]
    public void Plan_MalformedBindingAst_ShouldRetainDiagnosticCodes()
    {
        // Arrange
        GqlExpression[] expressions =
        [
            new GqlReduceExpression("",new GqlLiteralExpression(0L),"x",new GqlListExpression([]),new GqlLiteralExpression(0L)),
            new GqlListPredicateExpression(GqlListPredicateKind.All,"",new GqlListExpression([]),new GqlLiteralExpression(true)),
            new GqlReduceExpression("a",new GqlLiteralExpression(0L),"a",new GqlListExpression([]),new GqlLiteralExpression(0L)),
        ];
        // Act / Assert
        foreach (var expression in expressions)
        { Should.Throw<GraphPlanningException>(() => GraphValueExpressions.Children(expression).ToArray()).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG01018"); }
        var invalid = new GqlListPredicateExpression((GqlListPredicateKind)999, "x", new GqlListExpression([]), new GqlLiteralExpression(true));
        Should.Throw<GraphPlanningException>(() => GraphValueExpressions.Children(invalid).ToArray()).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05025");
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Binding cancellation: recorded callback progress cancels iteration and releases memory")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_CanceledIteration_ShouldRollbackAndReleaseAsync(bool reduce)
    {
        // Arrange
        using var canceled = new CancellationTokenSource();
        int visits = 0;
        var builder = GraphDatabaseEngine.CreateBuilder("binding-cancel");
        builder.Functions.Add(GraphScalarFunction.Create<long, long>("observe", value =>
        { if (++visits == 3) { canceled.Cancel(); } return value; }));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        string expression = reduce ? "reduce(a=0,x IN [1,2,3,4] | a+observe(x))" : "all(x IN [1,2,3,4] WHERE observe(x)>0)";
        // Act
        await Should.ThrowAsync<OperationCanceledException>(async () => await session.ExecuteAsync("INSERT (:Unwritten) RETURN " + expression, cancellationToken: canceled.Token));
        // Assert
        visits.ShouldBe(3);
        session.LastStatementMetrics!.Memory.RetainedBytes.ShouldBe(0);
        session.LastStatementMetrics.Memory.ActiveLeases.ShouldBe(0);
        (await RowAsync(session, "MATCH (n:Unwritten) RETURN count(*)")).ShouldBe(new object?[] { 0L });
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Dynamic predicates: only visited Boolean/NULL values are checked")]
    public async Task ExecuteAsync_UnknownPredicateKind_ShouldCheckVisitedValuesAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("dynamic-predicate", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:N {k:1})", cancellationToken: CancellationToken.None);
        // Act / Assert
        foreach (string name in new[] { "all", "any", "none", "single" })
        {
            (await RowAsync(session, $"MATCH (n:N) RETURN {name}(x IN NULL WHERE n.k)"))[0].ShouldBeNull();
            (await RowAsync(session, $"MATCH (n:N) RETURN {name}(x IN [] WHERE n.k)"))[0].ShouldBe(name is "all" or "none");
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync($"MATCH (n:N) RETURN {name}(x IN [1] WHERE n.k)", cancellationToken: CancellationToken.None));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG02013");
        }
    }

    private static async Task<object?[]> RowAsync(GraphDatabaseSession session, string query)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(query, cancellationToken: CancellationToken.None);
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        return rows.ShouldHaveSingleItem();
    }
}
