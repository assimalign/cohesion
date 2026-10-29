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
using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Protocol;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphSetQueryTests
{
    private readonly ITestOutputHelper _output;
    public GraphSetQueryTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Graph] - Composite queries: generated graphs agree with an independent multiset oracle")]
    [InlineData("UNION")]
    [InlineData("UNION ALL")]
    [InlineData("EXCEPT")]
    [InlineData("EXCEPT ALL")]
    [InlineData("INTERSECT")]
    [InlineData("INTERSECT ALL")]
    [InlineData("OTHERWISE")]
    public async Task ExecuteAsync_GeneratedGraphs_ShouldMatchMultisetOracleAsync(string op)
    {
        // Arrange: parallel edges, duplicate scalars/entities/paths, NULLs and nested values.
        await using var engine = GraphDatabaseEngine.Create("set-oracle", new());
        var random = new Random(17372);
        for (int sample = 0; sample < 8; sample++)
        {
            var database = await engine.CreateDatabaseAsync("g" + sample, CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            string seed = string.Join(",", Enumerable.Range(0, 7).Select(i => $"(n{i}:N {{k:{i},v:{(i % 3 == 0 ? "NULL" : random.Next(3).ToString(CultureInfo.InvariantCulture))}}})"));
            for (int i = 0; i < 13; i++) { seed += $",(n{random.Next(7)})-[:T]->(n{random.Next(7)})"; }
            await session.ExecuteAsync("INSERT " + seed, cancellationToken: CancellationToken.None);
            const string match = "MATCH p=(n:N)-[r:T]->(m) FOR copy IN [1,2] ";
            var source = await GraphExistsTests.RowsAsync(session, match + "RETURN n.k,m.k,n,r,p,n.v");
            foreach (var (expression, index) in new[] { ("n", 2), ("r", 3), ("p", 4), ("n.v", 5), ("NULL", -1), ("[n]", -2), ("{n:n}", -3) })
            {
                object? Project(object?[] row) => index switch
                { -1 => null, -2 => new object?[] { row[2] }, -3 => new Dictionary<string, object?> { ["n"] = row[2] }, _ => row[index] };
                int cutoff = sample == 0 ? -1 : sample == 1 ? 9 : 4;
                var left = source.Where(row => Convert.ToInt64(row[0]) < cutoff).Select(row => Key(Project(row))).ToList();
                var right = source.Where(row => Convert.ToInt64(row[1]) > 2).Select(row => Key(Project(row))).ToList();
                var expected = Oracle(left, right, op);
                // Act
                var actual = await GraphExistsTests.RowsAsync(session, match + $"FILTER n.k<{cutoff} RETURN {expression} AS x {op} " + match + $"FILTER m.k>2 RETURN {expression} AS x");
                // Assert: the oracle never uses GraphRowComparer or a hash-based multiset.
                actual.Select(row => Key(row[0])).Order(StringComparer.Ordinal).ShouldBe(expected.Order(StringComparer.Ordinal));
                session.LastStatementMetrics!.Memory.RetainedBytes.ShouldBe(0);
                session.LastStatementMetrics.Memory.ActiveLeases.ShouldBe(0);
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - OTHERWISE: right execution is lazy, errors propagate, and NULL rows are nonempty")]
    public async Task ExecuteAsync_WholeResultFallback_ShouldEvaluateOnlySelectedBranchAsync()
    {
        // Arrange
        int calls = 0;
        bool fail = true;
        var builder = GraphDatabaseEngine.CreateBuilder("fallback");
        builder.Functions.Add(GraphScalarFunction.Create<long, long>("fallback_probe", value =>
        { calls++; if (fail) { throw new InvalidOperationException("fallback sentinel"); } return value; }));
        await using var engine = await builder.BuildAsync();
        var database = await engine.CreateDatabaseAsync("g", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act / Assert
        (await GraphExistsTests.RowsAsync(session, "FOR x IN [1,1,NULL] RETURN x OTHERWISE RETURN fallback_probe(9) AS x"))
            .Select(row => row[0]).ShouldBe(new object?[] { 1L, 1L, null });
        (await GraphExistsTests.RowsAsync(session, "RETURN NULL AS x OTHERWISE RETURN fallback_probe(9) AS x"))[0][0].ShouldBeNull();
        calls.ShouldBe(0);
        var lateLeftError = await Should.ThrowAsync<DatabaseException>(async () =>
            await GraphExistsTests.RowsAsync(session, "FOR divisor IN [1,0] RETURN 1/divisor AS x OTHERWISE RETURN fallback_probe(9) AS x"));
        lateLeftError.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG02013");
        calls.ShouldBe(0);
        var leftError = await Should.ThrowAsync<DatabaseException>(async () =>
            await GraphExistsTests.RowsAsync(session, "RETURN fallback_probe(1) AS x OTHERWISE RETURN fallback_probe(2) AS x"));
        leftError.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05021");
        calls.ShouldBe(1);
        var rightError = await Should.ThrowAsync<DatabaseException>(async () =>
            await GraphExistsTests.RowsAsync(session, "FOR x IN [] RETURN x OTHERWISE RETURN fallback_probe(2) AS x"));
        rightError.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05021");
        calls.ShouldBe(2);
        fail = false;
        (await GraphExistsTests.RowsAsync(session, "FOR x IN [] RETURN x OTHERWISE FOR x IN [7,7] RETURN fallback_probe(x) AS x"))
            .Select(row => row[0]).ShouldBe(new object?[] { 7L, 7L });
        calls.ShouldBe(4);
        session.LastStatementMetrics!.Memory.RetainedBytes.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Composite queries: grouping, equal precedence, multiplicities and NEXT preserve bindings")]
    [InlineData("RETURN 1 AS x UNION RETURN 2 AS x INTERSECT RETURN 2 AS x", "2")]
    [InlineData("RETURN 1 AS x UNION (RETURN 2 AS x INTERSECT RETURN 2 AS x)", "1;2")]
    [InlineData("RETURN 1 AS x OTHERWISE RETURN 2 AS x UNION ALL RETURN 3 AS x", "1;3")]
    [InlineData("RETURN 1 AS x OTHERWISE (RETURN 2 AS x UNION ALL RETURN 3 AS x)", "1")]
    [InlineData("FOR x IN [1,1,1,2,3,NULL,NULL] RETURN x EXCEPT ALL FOR x IN [1,1,2,2,NULL] RETURN x", "1;3;NULL")]
    [InlineData("FOR x IN [1,1,1,2,3,NULL,NULL] RETURN x INTERSECT ALL FOR x IN [1,1,2,2,NULL] RETURN x", "1;1;2;NULL")]
    [InlineData("RETURN 1 AS x EXCEPT ALL RETURN 2 AS x INTERSECT DISTINCT RETURN 1.0 AS x", "1")]
    [InlineData("FOR x IN [2,1,1] RETURN x ORDER BY x LIMIT 2 EXCEPT ALL RETURN 1 AS x NEXT RETURN x*10 AS x", "10")]
    [InlineData("RETURN 4 AS x NEXT RETURN x AS x OTHERWISE RETURN 9 AS x", "4")]
    [InlineData("RETURN 4 AS x NEXT RETURN x AS x INTERSECT RETURN 4 AS x NEXT RETURN x+1 AS x", "5")]
    [InlineData("RETURN 1 AS x,2 AS y EXCEPT RETURN 1 AS x,3 AS y NEXT RETURN x+y AS z", "3")]
    [InlineData("RETURN {a:1,b:2} AS x INTERSECT RETURN {b:2,a:1.0} AS x NEXT RETURN x.a AS a", "1")]
    public async Task ExecuteAsync_CompositeTree_ShouldPreserveSemanticsAsync(string query, string expected)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("set-grouping", new());
        var database = await engine.CreateDatabaseAsync("g", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var rows = await GraphExistsTests.RowsAsync(session, query);
        // Assert
        string.Join(';', rows.Select(row => GraphExistsTests.Text(row[0]))).ShouldBe(expected);
        session.LastStatementMetrics!.Memory.RetainedBytes.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Composite queries: unselected and empty operands are validated before execution")]
    [InlineData("RETURN 1 AS x OTHERWISE RETURN $absent AS x", "CG05001")]
    [InlineData("RETURN 1 AS x OTHERWISE INSERT (:Forbidden) RETURN 2 AS x", "CG05029")]
    [InlineData("RETURN 1 AS x OTHERWISE RETURN 2 AS y", "CG01020")]
    [InlineData("RETURN 1 AS x OTHERWISE RETURN 'a' AS x", "CG02017")]
    [InlineData("RETURN 1 AS x EXCEPT RETURN 2 AS y", "CG01020")]
    [InlineData("RETURN 1 AS x INTERSECT RETURN 'a' AS x", "CG02017")]
    [InlineData("RETURN 1 AS x UNION RETURN 2 AS x EXCEPT (RETURN 3 AS x UNION ALL RETURN 4 AS x)", "CG00016")]
    public async Task ExecuteAsync_InvalidOperand_ShouldRefuseBeforeDataAccessAsync(string query, string code)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("set-admission", new());
        var database = await engine.CreateDatabaseAsync("g", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act / Assert
        foreach (string prefix in new[] { "", "EXPLAIN " })
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await GraphExistsTests.RowsAsync(session, prefix + query));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
            session.LastStatementMetrics!.Physical.ShouldBe(0);
            session.LastStatementMetrics.Memory.RetainedBytes.ShouldBe(0);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Set queries: work scales linearly and retained state has a coded refusal")]
    [InlineData("EXCEPT")]
    [InlineData("EXCEPT ALL")]
    [InlineData("INTERSECT")]
    [InlineData("INTERSECT ALL")]
    public async Task ExecuteAsync_HashState_ShouldHaveLinearWorkAndBoundedMemoryAsync(string op)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("set-accounting", new());
        var database = await engine.CreateDatabaseAsync("g", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        foreach (int size in new[] { 100, 200 })
        {
            var request = GraphQueryRequest.FromGql($"FOR x IN $xs RETURN x {op} FOR x IN $xs RETURN x",
                new Dictionary<string, object?> { ["xs"] = Enumerable.Range(0, size).ToArray() });
            // Act
            await using var result = (DatabaseResultSet)await session.ExecuteAsync(request, CancellationToken.None);
            int count = 0;
            await foreach (var _ in result.GetRowsAsync(CancellationToken.None)) { count++; }
            // Assert
            count.ShouldBe(op.StartsWith("EXCEPT", StringComparison.Ordinal) ? 0 : size);
            session.LastStatementMetrics!.Physical.ShouldBe(7L * size);
            _output.WriteLine($"{op}: rows/operand={size}; work={session.LastStatementMetrics.Physical}; peak={session.LastStatementMetrics.Memory.PeakRetainedBytes}");
            session.LastStatementMetrics.Memory.RetainedBytes.ShouldBe(0);
            session.LastStatementMetrics.Memory.ActiveLeases.ShouldBe(0);
        }
        await session.ExecuteAsync("FOR x IN $xs INSERT (:N {k:x})",
            new Dictionary<string, object?> { ["xs"] = Enumerable.Range(0, 2000).ToArray() }, CancellationToken.None);
        var large = GraphQueryRequest.FromGql($"MATCH (n:N) RETURN n.k AS x {op} RETURN -1 AS x",
            parameters: null, limits: new() { MemoryBytes = 100_000 });
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(large, CancellationToken.None));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05032");
        session.LastStatementMetrics!.Memory.RetainedBytes.ShouldBe(0);
        session.LastStatementMetrics.Memory.ActiveLeases.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Set queries: cancellation at recorded left, right and output work releases every owner")]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(19)]
    public async Task ExecuteAsync_CancelAtRecordedWork_ShouldReleaseStateAsync(int cancelAt)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("set-cancel", new());
        var database = await engine.CreateDatabaseAsync("g", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var budget = new GraphPlanExecutor.ExpansionBudget
        { OnConsume = work => { if (work == cancelAt) { cancellation.Cancel(); } } };
        var query = GraphQueryRequest.FromGql("FOR x IN [1,2,3] RETURN x INTERSECT ALL FOR x IN [1,2,3] RETURN x");
        // Act
        await Should.ThrowAsync<OperationCanceledException>(async () => await database.RunAsync(session, operation =>
        {
            operation.QueryBudget = budget;
            return GraphPlanExecutor.ExecuteAsync(database, operation, query.Statement, null, cancellation.Token);
        }, cancellation.Token));
        // Assert
        budget.Physical.ShouldBe(cancelAt);
        budget.Memory.RetainedBytes.ShouldBe(0);
        budget.Memory.ActiveLeases.ShouldBe(0);
        (await GraphExistsTests.RowsAsync(session, "RETURN 1"))[0][0].ShouldBe(1L);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Set queries: metadata covers skipped branches and EXPLAIN preserves every operator")]
    public async Task ExecuteAsync_MetadataAndExplain_ShouldCoverWholeTreeAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("set-metadata", new());
        var database = await engine.CreateDatabaseAsync("g", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        await using var result = (DatabaseResultSet)await session.ExecuteAsync("RETURN count(*) AS x OTHERWISE RETURN CAST(NULL AS FLOAT64) AS x", cancellationToken: CancellationToken.None);
        var explain = await GraphExistsTests.RowsAsync(session, "EXPLAIN RETURN 1 AS x EXCEPT ALL RETURN 2 AS x INTERSECT RETURN 1 AS x OTHERWISE RETURN 9 AS x");
        // Assert
        result.Columns.ShouldHaveSingleItem().Type.ShouldBe(DatabaseType.Float64);
        result.Columns[0].IsNullable.ShouldBeTrue();
        explain.Where(row => row[2] is "ExceptAll" or "Intersect" or "Otherwise").Select(row => row[2])
            .ShouldBe(new object?[] { "Otherwise", "Intersect", "ExceptAll" });
        ((string)explain[0][5]!).ShouldContain("lazy_right=True", Case.Sensitive);
        explain.ShouldContain(row => ((string)row[5]!).Contains("max(m-n,0)", StringComparison.Ordinal));
        session.LastStatementMetrics!.Physical.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Composite queries: set and fallback operands share the statement snapshot")]
    [InlineData("EXCEPT", 0)]
    [InlineData("INTERSECT", 1)]
    [InlineData("OTHERWISE", 1)]
    public async Task ExecuteAsync_LaterCommit_ShouldKeepStatementSnapshotAsync(string op, int expected)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("set-snapshot", new());
        var database = await engine.CreateDatabaseAsync("g", CancellationToken.None);
        await using var reader = await database.CreateSessionAsync(CancellationToken.None);
        await using var writer = await database.CreateSessionAsync(CancellationToken.None);
        await writer.ExecuteAsync("INSERT (:N)", cancellationToken: CancellationToken.None);
        string left = op == "OTHERWISE" ? "MATCH (n:N) FILTER FALSE RETURN n.k AS c" : "MATCH (n:N) RETURN count(*) AS c";
        var query = GraphQueryRequest.FromGql(left + " " + op + " MATCH (n:N) RETURN count(*) AS c");
        // Act: RunAsync has captured the snapshot before this commit.
        await using var result = (DatabaseResultSet)await database.RunAsync(reader, async operation =>
        {
            await writer.ExecuteAsync("INSERT (:N)", cancellationToken: CancellationToken.None);
            return await GraphPlanExecutor.ExecuteAsync(database, operation, query.Statement, null, CancellationToken.None);
        }, CancellationToken.None);
        var values = new List<object?>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { values.Add(row.GetValue(0)); }
        // Assert
        values.Count.ShouldBe(expected);
        if (expected != 0) { values[0].ShouldBe(1L); }
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Composite queries: long empty chains use an explicit execution stack")]
    public async Task ExecuteAsync_LongFallbackChain_ShouldRemainStackSafeAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("set-long-chain", new());
        var database = await engine.CreateDatabaseAsync("g", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        string query = string.Join(" OTHERWISE ", Enumerable.Repeat("FOR x IN [] RETURN x", 1024)) + " OTHERWISE RETURN 7 AS x";
        // Act
        var rows = await GraphExistsTests.RowsAsync(session, query);
        // Assert
        rows.ShouldHaveSingleItem()[0].ShouldBe(7L);
        session.LastStatementMetrics!.Memory.RetainedBytes.ShouldBe(0);
        session.LastStatementMetrics.Memory.ActiveLeases.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Composite queries: generated mixed trees agree with the multiset oracle")]
    public async Task ExecuteAsync_GeneratedOperatorTrees_ShouldMatchOracleAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("set-tree-oracle", new());
        var database = await engine.CreateDatabaseAsync("g", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var random = new Random(17373);
        for (int sample = 0; sample < 32; sample++)
        {
            string[] ops = [sample % 2 == 0 ? "UNION" : "UNION ALL", "EXCEPT", "EXCEPT ALL", "INTERSECT", "INTERSECT ALL", "OTHERWISE"];
            (string Query, List<string> Rows) Build(int depth)
            {
                if (depth == 0)
                {
                    string[] values = Enumerable.Range(0, random.Next(6))
                        .Select(_ => random.Next(4) is var n && n != 3 ? n.ToString(CultureInfo.InvariantCulture) : "NULL").ToArray();
                    return ($"FOR x IN [{string.Join(',', values)}] RETURN x", values.Select(value => value == "NULL" ? "null" : value).ToList());
                }
                var left = Build(depth - 1);
                var right = Build(depth - 1);
                string op = ops[random.Next(ops.Length)];
                return ($"({left.Query}) {op} ({right.Query})", Oracle(left.Rows, right.Rows, op));
            }
            var tree = Build(3);
            // Act
            var actual = await GraphExistsTests.RowsAsync(session, tree.Query);
            // Assert
            actual.Select(row => Key(row[0])).Order(StringComparer.Ordinal).ShouldBe(tree.Rows.Order(StringComparer.Ordinal), tree.Query);
            session.LastStatementMetrics!.Memory.RetainedBytes.ShouldBe(0);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Composite queries: operator attribution reports the actual root conjunction")]
    [InlineData("EXCEPT", "Except")]
    [InlineData("EXCEPT ALL", "ExceptAll")]
    [InlineData("INTERSECT", "Intersect")]
    [InlineData("INTERSECT ALL", "IntersectAll")]
    [InlineData("OTHERWISE", "Otherwise")]
    public async Task DescribeOperator_SetRoot_ShouldReportConjunctionAsync(string op, string expected)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("set-attribution", new());
        var database = await engine.CreateDatabaseAsync("g", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var query = GraphQueryRequest.FromGql($"RETURN 1 AS x {op} RETURN 2 AS x");
        // Act
        var plan = await database.RunAsync(session, operation => new ValueTask<GraphCompositePlan>(
            new GraphPlanner(database, operation.Context.Snapshot).PlanComposite(query.Statement.GqlExpression,
                null, false, CancellationToken.None)), CancellationToken.None);
        // Assert
        GraphPlanner.DescribeOperator(plan).ShouldBe(expected);
    }

    private static List<string> Oracle(List<string> left, List<string> right, string op)
    {
        if (op == "OTHERWISE") { return left.Count == 0 ? right : left; }
        if (op.StartsWith("UNION", StringComparison.Ordinal))
        { return op == "UNION ALL" ? [.. left, .. right] : left.Concat(right).Distinct(StringComparer.Ordinal).ToList(); }
        var remaining = new List<string>(right);
        var result = new List<string>();
        foreach (string row in left)
        {
            int match = remaining.IndexOf(row);
            bool include = op.StartsWith("EXCEPT", StringComparison.Ordinal) ? match < 0 : match >= 0;
            if (op.EndsWith(" ALL", StringComparison.Ordinal) && match >= 0) { remaining.RemoveAt(match); }
            if (include && (op.EndsWith(" ALL", StringComparison.Ordinal) || !result.Contains(row))) { result.Add(row); }
        }
        return result;
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
