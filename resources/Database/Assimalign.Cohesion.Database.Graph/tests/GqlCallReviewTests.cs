using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GqlCallReviewTests
{
    private readonly ITestOutputHelper _output;

    public GqlCallReviewTests(ITestOutputHelper output) { _output = output; }

    public static IEnumerable<object[]> FunctionCases()
    {
        foreach (bool optional in new[] { false, true })
        {
            foreach (bool arithmetic in new[] { false, true })
            {
                yield return [optional, arithmetic, "upper(y)", "'ab'", "NULL+1", "AB", "NULL", "NULL"];
                yield return [optional, arithmetic, "toString(y)", "1", "x+0", "1", "1", "NULL"];
                yield return [optional, arithmetic, "size(y)", "'ab'", "NULL+1", "2", "NULL", "NULL"];
                yield return [optional, arithmetic, "f(y)", "1", "x+0", "2", "2", "NULL"];
                yield return [optional, arithmetic, "all(i IN y WHERE i>0)", "NULL", "NULL+1", "NULL", "NULL", "NULL"];
                yield return [optional, arithmetic, "size([1,2,3][y..])", "1", "x+0", "2", "2", "NULL"];
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - CALL unknown output kinds admit scalar, list and slice consumers (#1807)")]
    [MemberData(nameof(FunctionCases))]
    public async Task Execute_UnknownCallKind_ShouldReturnRows(bool optional, bool arithmetic, string consumer,
        string propertyValue, string expression, string propertyResult, string arithmeticResult, string absentResult)
    {
        // Arrange
        var builder = GraphDatabaseEngine.CreateBuilder("call-kinds");
        builder.Functions.Add(GraphScalarFunction.Create<long, long>("f", value => value + 1));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:N {k:1,v:" + propertyValue + "})", cancellationToken: CancellationToken.None);
        string body = arithmetic ? "FILTER x=1 RETURN " + expression + " AS y" : "MATCH (n:N {k:x}) RETURN n.v AS y";
        string source = "FOR x IN [1,3] " + (optional ? "OPTIONAL " : "") + "CALL (x) { " + body + " } RETURN " + consumer;
        // Act
        string actual = await Rows(session, source);
        // Assert
        actual.ShouldBe((arithmetic ? arithmeticResult : propertyResult) + (optional ? ";" + absentResult : ""));
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - NULL CALL literals have the same consumer semantics as LET (#1807)")]
    [InlineData("upper(y)")]
    [InlineData("toString(y)")]
    [InlineData("size(y)")]
    [InlineData("f(y)")]
    [InlineData("all(i IN y WHERE i>0)")]
    [InlineData("size([1,2,3][y..])")]
    public async Task Execute_NullCallLiteral_ShouldMatchLet(string consumer)
    {
        // Arrange
        var builder = GraphDatabaseEngine.CreateBuilder("call-null");
        builder.Functions.Add(GraphScalarFunction.Create<long, long>("f", value => value + 1));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act / Assert
        (await Rows(session, "CALL () { RETURN NULL AS y } RETURN " + consumer))
            .ShouldBe(await Rows(session, "LET y=NULL RETURN " + consumer));
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - CALL refusals retain conservative imports and deleted-variable diagnostics (#1807)")]
    [InlineData("LET a=10 CALL { WITH a WITH 2 AS b RETURN a AS x } RETURN a,x", "CK00001")]
    [InlineData("LET a=10 CALL { WITH a WITH 2 AS a RETURN a AS x } RETURN *", "CK00001")]
    [InlineData("LET a=10 CALL { WITH a RETURN 2 AS b NEXT RETURN a AS x } RETURN x", "CK00001")]
    [InlineData("LET a=10 CALL { WITH a RETURN 2 AS b NEXT (RETURN a AS x UNION RETURN a AS x) } RETURN x", "CK00001")]
    [InlineData("LET x=1 CALL (x) { WITH 2 AS y RETURN * } RETURN x,y", "CG01024")]
    [InlineData("MATCH (n) DELETE n CALL (n) { RETURN 1 AS y } RETURN y", "CG03003")]
    public async Task Execute_ImportBoundary_ShouldReturnCodedRefusal(string source, string code)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("call-imports", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act / Assert
        foreach (string prefix in new[] { "", "EXPLAIN " })
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(prefix + source, cancellationToken: CancellationToken.None));
            error.Diagnostics.Single(d => d.Severity == DiagnosticSeverity.Error).Code.ShouldBe(code);
            (await Rows(session, "RETURN 7")).ShouldBe("7");
        }
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - malformed importing WITH uses the planner diagnostic (#1807)")]
    public async Task Execute_NullImportProjections_ShouldRefuseWithDiagnostic()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("call-malformed", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var body = new GqlQueryExpression(new GqlStage[] { new GqlReturnStage(null!, IsWith: true),
            new GqlReturnStage([new GqlProjection(new GqlLiteralExpression(1L), "y")]) });
        var query = new GqlQueryExpression(new GqlStage[] { new GqlCallStage(body),
            new GqlReturnStage([new GqlProjection(new GqlVariableExpression("y"))]) });
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(new GraphQueryRequest(new(query)), CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05001");
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - parsed nested CALL and NEXT planning checks properties linearly (#1807)")]
    [InlineData("next")]
    [InlineData("import")]
    [InlineData("parenthesized")]
    public async Task Plan_ParsedNesting_ShouldCheckPropertiesLinearly(string form)
    {
        // Arrange
        const int depth = 20;
        string source = "RETURN 1 AS b";
        for (int i = 0; i < depth; i++)
        {
            string exists = "EXISTS { MATCH (n) WHERE n.k" + i + "=1 }";
            source = form switch
            {
                "import" => "LET x" + i + "=1 CALL { WITH x" + i + " LET a" + i + "=" + exists + " CALL () { " + source + " } RETURN b } RETURN b",
                "parenthesized" => "(RETURN " + exists + " AS a" + i + " NEXT CALL () { " + source + " } RETURN b) UNION RETURN 1 AS b",
                _ => "RETURN " + exists + " AS a" + i + " NEXT CALL () { " + source + " } RETURN b",
            };
        }
        source = "CALL () { " + source + " } RETURN b";
        var query = GraphQueryRequest.FromGql(source).Statement.GqlExpression;
        await using var engine = GraphDatabaseEngine.Create("call-linear", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        int checks = 0;
        database.Catalog.BeforePropertyCheck = () =>
        {
            checks++;
            checks.ShouldBeLessThanOrEqualTo(depth * 4, "planning must stop before exponential repetition");
        };
        // Act
        try
        {
            await database.RunAsync(session, operation => ValueTask.FromResult(new GraphPlanner(database, operation.Context.Snapshot).Plan(query)), CancellationToken.None);
        }
        finally { database.Catalog.BeforePropertyCheck = null; }
        // Assert
        _output.WriteLine($"CALL planning ({form}): depth={depth}; property checks={checks}");
        checks.ShouldBeInRange(depth, depth * 2);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - sequential uncorrelated CALL planning allocation scales linearly (#1807)")]
    public async Task Plan_SequentialCalls_ShouldAllocateLinearly()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("call-allocations", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var small = Query(100); var large = Query(1000);
        // Act
        var bytes = await database.RunAsync(session, operation =>
        {
            Measure(small); Measure(large);
            return ValueTask.FromResult((Small: Measure(small), Large: Measure(large)));

            long Measure(GqlQueryExpression query)
            {
                long minimum = long.MaxValue;
                for (int round = 0; round < 3; round++)
                {
                    long start = GC.GetAllocatedBytesForCurrentThread();
                    var plan = new GraphPlanner(database, operation.Context.Snapshot).Plan(query);
                    long size = GC.GetAllocatedBytesForCurrentThread() - start;
                    plan.Stages.Count.ShouldBe(query.Stages.Count);
                    GC.KeepAlive(plan);
                    minimum = Math.Min(minimum, size);
                }
                return minimum;
            }
        }, CancellationToken.None);
        // Assert: allow runtime/platform variation, but not the quadratic ~100x slope.
        _output.WriteLine($"CALL planning allocations: count 100={bytes.Small}; count 1000={bytes.Large}; ratio={(double)bytes.Large/bytes.Small:F3}");
#if !DEBUG
        bytes.Large.ShouldBeLessThan(bytes.Small * 18);
#endif
        static GqlQueryExpression Query(int count) => GraphQueryRequest.FromGql(string.Concat(Enumerable.Range(0, count)
            .Select(i => "CALL { RETURN 1 AS a" + i + " } ")) + "RETURN 1").Statement.GqlExpression;
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - parsed CALL paths retain parsed depth admission after NEXT and import normalization (#1807)")]
    [InlineData("next", false)]
    [InlineData("next", true)]
    [InlineData("import", false)]
    [InlineData("import", true)]
    [InlineData("parenthesized", false)]
    [InlineData("parenthesized", true)]
    public async Task Plan_ParsedCallPaths_ShouldAdmitBeyondDirectAstCap(string form, bool correlated)
    {
        // Arrange
        string pattern = new string('(', 129) + "(a:N)->(b)" + new string(')', 129);
        string body = correlated ? "RETURN EXISTS { MATCH " + pattern + " } AS y" : "MATCH " + pattern + " RETURN b.k AS y";
        string source = form switch
        {
            "import" => "LET x=1 CALL { WITH x " + body + " } RETURN y",
            "parenthesized" => "(RETURN 1 AS z NEXT " + body + ") UNION " + body,
            _ => "CALL () { RETURN 1 AS z NEXT " + body + " } RETURN y",
        };
        await using var engine = GraphDatabaseEngine.Create("call-path-depth", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act / Assert
        await using var result = (DatabasePlanDescription)await session.ExecuteAsync("EXPLAIN " + source, cancellationToken: CancellationToken.None);
        int nodes = 0;
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { nodes++; }
        nodes.ShouldBeGreaterThan(0);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - multi-row CALL invocation operators and imported entities remain local (#1807)")]
    [InlineData("FOR x IN [1,2] CALL (x) { FOR z IN [3,1,2] RETURN z+x AS y LIMIT 2 } RETURN x,y", "1|4;1|2;2|5;2|3")]
    [InlineData("FOR x IN [1,2] CALL (x) { FOR z IN [3,1,2] RETURN z+x AS y ORDER BY y } RETURN x,y", "1|2;1|3;1|4;2|3;2|4;2|5")]
    [InlineData("FOR x IN [1,2] CALL (x) { FOR z IN [1,1,2] RETURN DISTINCT z+x AS y } RETURN x,y", "1|2;1|3;2|3;2|4")]
    [InlineData("FOR x IN [1,2] CALL (x) { FOR z IN [1,2,3] RETURN sum(z)+x AS y } RETURN x,y", "1|7;2|8")]
    [InlineData("MATCH (n:N {k:1}) CALL (n) { RETURN n.k AS y } RETURN y", "1")]
    [InlineData("MATCH ()-[r:T]->() CALL (r) { RETURN r.k AS y } RETURN y", "3")]
    [InlineData("MATCH p=(a:N)-[:T]->(b:N) CALL (p) { RETURN path_length(p) AS y } RETURN y", "1")]
    [InlineData("OPTIONAL MATCH (n:N {k:99}) OPTIONAL CALL (n) { MATCH (n) RETURN n.k AS y } RETURN n IS NULL,y", "True|NULL")]
    [InlineData("CALL () { RETURN 4 AS k } INSERT (:N {k:k}) RETURN k", "4")]
    [InlineData("CALL () { MATCH (n:N {k:1}) RETURN n AS entity } SET entity.k=4 RETURN entity.k", "4")]
    public async Task Execute_CallOperators_ShouldKeepInvocationState(string source, string expected)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("call-operators", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (a:N {k:1})-[:T {k:3}]->(b:N {k:2})", cancellationToken: CancellationToken.None);
        // Act / Assert
        (await Rows(session, source)).ShouldBe(expected);
        session.LastStatementMetrics!.Memory.ActiveLeases.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - OPTIONAL CALL unknown labels retain NULL rows and their warning (#1807)")]
    public async Task Execute_UnknownOptionalLabel_ShouldWarnAndExtend()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("call-warning", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        await using var result = (DatabaseResultSet)await session.ExecuteAsync("OPTIONAL CALL () { MATCH (n:Absent) RETURN 1 AS y } RETURN y", cancellationToken: CancellationToken.None);
        // Assert
        var warning = result.Diagnostics.ShouldHaveSingleItem();
        warning.Code.ShouldBe("CG01010"); warning.Severity.ShouldBe(DiagnosticSeverity.Warning);
        int rows = 0;
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { row.GetValue(0).ShouldBeNull(); rows++; }
        rows.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - CALL runtime failures preserve code and inner expression location (#1807)")]
    public async Task Execute_BodyRuntimeError_ShouldPreserveLocation()
    {
        // Arrange
        const string source = "CALL () { FOR z IN [0] RETURN 1/z AS y } RETURN y";
        await using var engine = GraphDatabaseEngine.Create("call-error", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(source, cancellationToken: CancellationToken.None));
        // Assert
        var diagnostic = error.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("CG02013");
        diagnostic.Start.ShouldBe(source.IndexOf("1/z", StringComparison.Ordinal));
        diagnostic.Length.ShouldBe(3);
        (await Rows(session, "RETURN 7")).ShouldBe("7");
    }

    private static async Task<string> Rows(GraphDatabaseSession session, string source)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(source, cancellationToken: CancellationToken.None);
        var rows = new List<string>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { rows.Add(string.Join('|', Enumerable.Range(0, row.FieldCount).Select(i => row.GetValue(i) is { } value ? Convert.ToString(value, CultureInfo.InvariantCulture) : "NULL"))); }
        return string.Join(';', rows);
    }
}
