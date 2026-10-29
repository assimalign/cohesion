using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;
using Xunit.Abstractions;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Graph.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphUnionTests
{
    private readonly ITestOutputHelper _output;
    public GraphUnionTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Graph] - UNION: generated graph results agree with a structural bag/set oracle")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_GeneratedGraphs_ShouldMatchOracleAsync(bool all)
    {
        // Arrange: each graph has duplicate property values, parallel edges and NULLs.
        await using var engine = GraphDatabaseEngine.Create("union-oracle", new());
        var random = new Random(1737);
        for (int sample = 0; sample < 12; sample++)
        {
            var database = await engine.CreateDatabaseAsync("g" + sample, CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            string seed = string.Join(", ", Enumerable.Range(0, 8).Select(i => $"(n{i}:N {{k:{i},v:{(i % 3 == 0 ? "NULL" : random.Next(3).ToString(CultureInfo.InvariantCulture))}}})"));
            for (int edge = 0; edge < 14; edge++) { seed += $", (n{random.Next(8)})-[:T {{w:{edge % 3}}}]->(n{random.Next(8)})"; }
            await session.ExecuteAsync("INSERT " + seed, cancellationToken: CancellationToken.None);
            string match = "MATCH p=(n:N)-[r:T]->(m) ";
            var paths = await GraphExistsTests.RowsAsync(session, match + "RETURN n.k,m.k,n,r,p,n.v");
            foreach (var (expression, index) in new[] { ("n", 2), ("r", 3), ("p", 4), ("n.v", 5), ("NULL", -1), ("[n]", -2), ("{n:n}", -3) })
            {
                object? Project(object?[] row) => index switch { -1 => null, -2 => new object?[] { row[2] }, -3 => new Dictionary<string, object?> { ["n"] = row[2] }, _ => row[index] };
                var expected = paths.Where(row => Convert.ToInt64(row[0]) < 5).Select(Project)
                    .Concat(paths.Where(row => Convert.ToInt64(row[1]) > 2).Select(Project)).Select(Key);
                if (!all) { expected = expected.Distinct(StringComparer.Ordinal); }
                // Act
                var actual = await GraphExistsTests.RowsAsync(session, match + "WHERE n.k<5 RETURN " + expression + " AS x UNION" + (all ? " ALL " : " ") + match + "WHERE m.k>2 RETURN " + expression + " AS x");
                // Assert: an independent recursive key compares entity identity and path order.
                actual.Select(row => Key(row[0])).Order(StringComparer.Ordinal).ShouldBe(expected.Order(StringComparer.Ordinal));
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - UNION: validation refuses every operand before data access")]
    [InlineData("RETURN 1 AS x UNION INSERT (:Forbidden) RETURN 1 AS x", "CG05029")]
    [InlineData("INSERT (:Forbidden) RETURN 1 AS x UNION RETURN 1 AS x", "CG05029")]
    [InlineData("MATCH (n) SET n.x=1 RETURN n AS x UNION MATCH (n) RETURN n AS x", "CG05029")]
    [InlineData("MATCH (n) REMOVE n.x RETURN n AS x UNION MATCH (n) RETURN n AS x", "CG05029")]
    [InlineData("RETURN 1 AS x UNION MATCH (n) DELETE n", "CG05029")]
    [InlineData("MERGE (n:Forbidden) RETURN n AS x UNION MATCH (n) RETURN n AS x", "CG05029")]
    [InlineData("RETURN 1 AS x UNION RETURN 2 AS x UNION ALL RETURN 3 AS x", "CG00016")]
    [InlineData("RETURN 1 AS x UNION (RETURN 2 AS x UNION ALL RETURN 3 AS x)", "CG00016")]
    [InlineData("RETURN 1 AS x UNION RETURN 2 AS y", "CG01020")]
    [InlineData("RETURN 1 AS x,2 AS y UNION RETURN 2 AS y,1 AS x", "CG01020")]
    [InlineData("RETURN 1 AS x UNION RETURN 2 AS x,3 AS y", "CG01020")]
    [InlineData("RETURN 1 AS x UNION RETURN 'a' AS x", "CG02017")]
    [InlineData("RETURN [] AS x UNION RETURN {} AS x", "CG02017")]
    [InlineData("RETURN [] AS x UNION MATCH (n) RETURN [n] AS x UNION MATCH ()-[r]->() RETURN [r] AS x", "CG02017")]
    [InlineData("MATCH (n) RETURN n AS x UNION RETURN 1 AS x", "CG02017")]
    [InlineData("RETURN NULL AS x UNION RETURN 1 AS x UNION RETURN 'a' AS x", "CG02017")]
    [InlineData("LET local=1 RETURN local AS x UNION RETURN local AS x", "CG05001")]
    [InlineData("RETURN 1/0 AS x UNION RETURN $missing AS x", "CG05001")]
    [InlineData("RETURN 1/0 AS x UNION MATCH (n) WHERE EXISTS { (n)->(m) WHERE absent=1 } RETURN 1 AS x", "CG05001")]
    public async Task ExecuteAsync_InvalidComposite_ShouldRefuseBeforeExecutionAsync(string query, string code)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("union-invalid", new());
        var database = await engine.CreateDatabaseAsync("g", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act / Assert
        foreach (string prefix in new[] { "", "EXPLAIN " })
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(prefix + query, cancellationToken: CancellationToken.None));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
            session.LastStatementMetrics!.Physical.ShouldBe(0);
            session.LastStatementMetrics.Memory.RetainedBytes.ShouldBe(0);
        }
        (await GraphExistsTests.RowsAsync(session, "MATCH (n:Forbidden) RETURN count(*)"))[0][0].ShouldBe(0L);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - UNION: grouping, lexical subplans and local paging compose")]
    [InlineData("FOR x IN [3,1,2] RETURN x ORDER BY x SKIP 1 LIMIT 1 UNION ALL FOR x IN [9,8] RETURN x ORDER BY x LIMIT 1", "2;8")]
    [InlineData("RETURN 1 AS x UNION (RETURN 1 AS x UNION RETURN 2 AS x)", "1;2")]
    [InlineData("(LET x=1 RETURN x NEXT RETURN x) UNION ALL LET x=2 RETURN x", "1;2")]
    [InlineData("(RETURN 1 AS x NEXT RETURN x+1 AS x) UNION ALL (RETURN 2 AS x NEXT RETURN x+2 AS x)", "2;4")]
    [InlineData("RETURN NULL AS x UNION RETURN NULL AS x", "NULL")]
    [InlineData("RETURN 1 AS x UNION RETURN 1.0 AS x", "1")]
    [InlineData("RETURN {a:1,b:2} AS x UNION RETURN {b:2,a:1} AS x", "map")]
    public async Task ExecuteAsync_CompositeOperands_ShouldPreserveSemanticsAsync(string query, string expected)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("union-values", new());
        var database = await engine.CreateDatabaseAsync("g", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var rows = await GraphExistsTests.RowsAsync(session, query);
        // Assert
        if (expected == "map") { rows.ShouldHaveSingleItem(); }
        else { string.Join(';', rows.Select(row => GraphExistsTests.Text(row[0]))).ShouldBe(expected); }
        session.LastStatementMetrics!.Memory.RetainedBytes.ShouldBe(0);
        session.LastStatementMetrics.Memory.ActiveLeases.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - UNION: runtime shape and shared work refusals release all state")]
    public async Task ExecuteAsync_DynamicShapeAndWorkLimit_ShouldRefuseAndCleanUpAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("union-dynamic", new());
        var database = await engine.CreateDatabaseAsync("g", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var workLimited = GraphQueryRequest.FromGql("FOR x IN [1,2] RETURN x UNION ALL FOR x IN [3,4] RETURN x", null, new() { WorkUnits = 6 });
        // Act / Assert
        foreach (var (request, code) in new[]
        {
            (GraphQueryRequest.FromGql("FOR x IN [1,'a'] RETURN x UNION RETURN NULL AS x"), "CG02017"),
            (workLimited, "CG05004"),
        })
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(request, CancellationToken.None));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
            session.LastStatementMetrics!.Memory.RetainedBytes.ShouldBe(0);
            session.LastStatementMetrics.Memory.ActiveLeases.ShouldBe(0);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - UNION: correlated operands retain occurrence binding and EXPLAIN grouping")]
    public async Task ExecuteAsync_CorrelatedOperands_ShouldRetainSubplansAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("union-correlated", new());
        var database = await engine.CreateDatabaseAsync("g", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(GraphExistsTests.Seed, cancellationToken: CancellationToken.None);
        const string query = "MATCH (a:N {k:1}) WHERE EXISTS { (a)->(b) } RETURN [(a)->(b) | b.k] AS xs UNION (MATCH (a:N {k:2}) RETURN [(a)->(b) | b.k] AS xs UNION RETURN [] AS xs)";
        // Act
        var rows = await GraphExistsTests.RowsAsync(session, query);
        var description = await GraphExistsTests.RowsAsync(session, "EXPLAIN " + query);
        // Assert
        rows.Count.ShouldBe(3);
        description.Count(row => Equals(row[2], "Union")).ShouldBe(2);
        description.ShouldContain(row => Equals(row[2], "SemiApply"));
        description.Count(row => Equals(row[2], "CollectApply")).ShouldBe(2);
        description.Where(row => Equals(row[2], "Union")).ShouldAllBe(row => ((string)row[5]!).Contains("memory_refusal=CG05030", StringComparison.Ordinal));
        var allDescription = await GraphExistsTests.RowsAsync(session, "EXPLAIN RETURN 1 AS x UNION ALL RETURN 2 AS x");
        ((string)allDescription[0][5]!).ShouldContain("memory_refusal=CG05020", Case.Sensitive);
        session.LastStatementMetrics!.Physical.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - UNION: a reused AST binds correlated occurrences in each isolated operand")]
    public async Task ExecuteAsync_SharedAst_ShouldBindEachOperandAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("union-ast", new());
        var database = await engine.CreateDatabaseAsync("g", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(GraphExistsTests.Seed, cancellationToken: CancellationToken.None);
        var inner = GraphQueryRequest.FromGql("MATCH (a:N) RETURN EXISTS { (a)->(b) } AS x").Statement.GqlExpression;
        var query = new GqlQueryExpression(new GqlCompositeQuery(inner, GqlCompositeOperator.Union, inner));
        // Act
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(new GraphQueryRequest(new GqlQueryStatement(query)), CancellationToken.None);
        // Assert
        var values = new List<object?>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { values.Add(row.GetValue(0)); }
        values.OrderBy(value => value).ShouldBe(new object?[] { false, true });
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - UNION: work is linear and distinct storage is charged and released")]
    public async Task ExecuteAsync_LargeComposite_ShouldAccountForRowsAndDistinctMemoryAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("union-accounting", new());
        var database = await engine.CreateDatabaseAsync("g", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        foreach (bool all in new[] { true, false })
        {
            long previous = 0;
            foreach (int size in new[] { 100, 200 })
            {
                var parameters = new Dictionary<string, object?> { ["xs"] = Enumerable.Range(0, size).ToArray() };
                var request = GraphQueryRequest.FromGql("FOR x IN $xs RETURN x UNION" + (all ? " ALL " : " ") + "FOR x IN $xs RETURN x", parameters);
                // Act
                await using var result = (DatabaseResultSet)await session.ExecuteAsync(request, CancellationToken.None);
                int count = 0;
                await foreach (var _ in result.GetRowsAsync(CancellationToken.None)) { count++; }
                // Assert
                count.ShouldBe(all ? size * 2 : size);
                long work = session.LastStatementMetrics!.Physical;
                work.ShouldBe(4L * size);
                if (previous != 0) { work.ShouldBe(previous * 2); }
                previous = work;
                _output.WriteLine($"{(all ? "UNION ALL" : "UNION")}: input={size * 2}; work={work}; peak={session.LastStatementMetrics.Memory.PeakRetainedBytes}");
                session.LastStatementMetrics.Memory.RetainedBytes.ShouldBe(0);
            }
        }
        var large = GraphQueryRequest.FromGql("FOR x IN $xs RETURN x UNION RETURN -1 AS x",
            new Dictionary<string, object?> { ["xs"] = Enumerable.Range(0, 5000).ToArray() }, new() { MemoryBytes = 300_000 });
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(large, CancellationToken.None));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05030");
        session.LastStatementMetrics!.Memory.RetainedBytes.ShouldBe(0);
        session.LastStatementMetrics.Memory.ActiveLeases.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - UNION: reduce planning work is shared across operand scopes")]
    public async Task ExecuteAsync_ManyReductions_ShouldSharePlanningCapAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("union-reduce-cap", new());
        var database = await engine.CreateDatabaseAsync("g", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        const string operand = "RETURN reduce(a=0,x IN [1,2] | a+x) AS x";
        (await GraphExistsTests.RowsAsync(session, operand))[0][0].ShouldBe(3L);
        string composite = string.Join(" UNION ALL ", Enumerable.Repeat(operand, 8192));
        // Act / Assert
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(composite, cancellationToken: CancellationToken.None));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05026");
        session.LastStatementMetrics!.Physical.ShouldBe(0);
    }

    private static string Key(object? value) => value switch
    {
        null => "null", GraphNode node => "node:" + node.Id, GraphRelationship edge => "edge:" + edge.Id,
        GraphPath path => "path:" + string.Join(',', path.Nodes.Select(node => node.Id)) + "/" + string.Join(',', path.Relationships.Select(edge => edge.Id)),
        IReadOnlyDictionary<string, object?> map => "map:" + string.Join(',', map.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + Key(pair.Value))),
        IList list => "list:" + string.Join(',', list.Cast<object?>().Select(Key)),
        IFormattable scalar => scalar.ToString(null, CultureInfo.InvariantCulture), _ => value.ToString()!,
    };
}
