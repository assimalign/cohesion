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

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphListComprehensionTests
{
    [Theory(DisplayName = "Cohesion Test [Graph] - Comprehensions: filters, identity and projection retain source order")]
    [InlineData("[x IN [1,2,3]]", "1,2,3")]
    [InlineData("[x IN [1,2,3] WHERE x > 1]", "2,3")]
    [InlineData("[x IN [1,2,3] | x * 2]", "2,4,6")]
    [InlineData("[x IN [1,2,3] WHERE x > 1 | x * 2]", "4,6")]
    [InlineData("[x IN [] | 1/0]", "")]
    [InlineData("[x IN [1,2] WHERE NULL | 1/0]", "")]
    [InlineData("[x IN [1,2] WHERE FALSE | 1/0]", "")]
    [InlineData("[x IN [NULL,2] WHERE x IS NOT NULL]", "2")]
    [InlineData("[sum IN [1,2] | sum + 1]", "2,3")]
    public async Task ExecuteAsync_Comprehension_ShouldReturnOrderedValuesAsync(string expression, string expected)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("list-values", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var value = await ValueAsync(session, "RETURN " + expression);
        // Assert
        string.Join(',', value.ShouldBeOfType<object?[]>()).ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Slices: bounds clamp and negative offsets count from the end")]
    [InlineData("[0..]", "0,1,2,3")]
    [InlineData("[1..3]", "1,2")]
    [InlineData("[..2]", "0,1")]
    [InlineData("[2..]", "2,3")]
    [InlineData("[-2..]", "2,3")]
    [InlineData("[..-1]", "0,1,2")]
    [InlineData("[-3..-1]", "1,2")]
    [InlineData("[-99..99]", "0,1,2,3")]
    [InlineData("[99..]", "")]
    [InlineData("[..-99]", "")]
    [InlineData("[3..1]", "")]
    [InlineData("[2..2]", "")]
    [InlineData("[-2147483648..2147483647]", "0,1,2,3")]
    public async Task ExecuteAsync_Slice_ShouldMatchReferenceBoundsAsync(string suffix, string expected)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("list-slice", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var value = await ValueAsync(session, "RETURN [0,1,2,3]" + suffix);
        // Assert
        string.Join(',', value.ShouldBeOfType<object?[]>()).ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Collections: NULL source and explicit NULL bounds yield NULL")]
    [InlineData("[x IN NULL WHERE TRUE | x]")]
    [InlineData("NULL[0..]")]
    [InlineData("[1,2][NULL..]")]
    [InlineData("[1,2][..NULL]")]
    [InlineData("[1,2][NULL..NULL]")]
    public async Task ExecuteAsync_NullCollection_ShouldReturnNullAsync(string expression)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("list-null", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act / Assert
        (await ValueAsync(session, "RETURN " + expression)).ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Collections: invalid list operations and iterator escapes are coded")]
    [InlineData("[x IN 1 | x]", "CG02014")]
    [InlineData("[x IN 'abc' | x]", "CG02014")]
    [InlineData("1[0..]", "CG02014")]
    [InlineData("[1]['x'..]", "CG02014")]
    [InlineData("[1][2147483648..]", "CG02014")]
    [InlineData("[1][..-2147483649]", "CG02014")]
    [InlineData("[x IN [1] WHERE 1]", "CG02013")]
    [InlineData("[x IN [1] | absent]", "CG05001")]
    [InlineData("[x IN [] | absent]", "CG05001")]
    [InlineData("[x IN NULL WHERE absent | x]", "CG05001")]
    [InlineData("[x IN x | x]", "CG05001")]
    [InlineData("[x IN [1] | x], x", "CG05001")]
    [InlineData("[x IN [1] | sum(x)]", "CG05024")]
    public async Task ExecuteAsync_InvalidCollection_ShouldReportDiagnosticAsync(string expression, string code)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("list-invalid", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("RETURN " + expression, cancellationToken: CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
        (await ValueAsync(session, "RETURN 42")).ShouldBe(42L);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Iterator scopes: nested shadowing restores outer values and supports grouped sources")]
    public async Task ExecuteAsync_NestedScopes_ShouldKeepLexicalBindingsAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("list-scopes", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act / Assert
        var values = (await ValueAsync(session, "LET x = [1,2] RETURN [x IN x | [x IN [x+10] | x][0]]")).ShouldBeOfType<object?[]>();
        values.ShouldBe(new object?[] { 11L, 12L });
        (await ValueAsync(session, "LET x = 99 RETURN [x IN [1,2] | [y IN [10] | x+y][0]][1] + x")).ShouldBe(111L);
        (await ValueAsync(session, "FOR v IN [1,2,3] RETURN [x IN collect(v) WHERE x > 1 | x*2]")).ShouldBeOfType<object?[]>().ShouldBe(new object?[] { 4L, 6L });
        (await ValueAsync(session, "FOR v IN [1,1] WITH v, collect(v) AS vs RETURN [x IN vs | v + x]")).ShouldBeOfType<object?[]>().ShouldBe(new object?[] { 2L, 2L });
        (await ValueAsync(session, "LET x = 99 RETURN [x IN [1,2] | x] AS x ORDER BY [x IN x | x][0]")).ShouldBeOfType<object?[]>().ShouldBe(new object?[] { 1L, 2L });
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Collection budgets: nested work and temporary lists share the statement grant")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_CollectionLimit_ShouldRollbackAndReleaseAsync(bool memory)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("list-budgets", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var parameters = new Dictionary<string, object?> { ["xs"] = Enumerable.Range(0, 100).ToArray() };
        var request = GraphQueryRequest.FromGql("INSERT (:Unwritten) RETURN [x IN $xs | [y IN $xs | y]]", parameters,
            memory ? new() { MemoryBytes = 8192 } : new() { WorkUnits = 50 });
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(request, CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(memory ? "CG05020" : "CG05004");
        session.LastStatementMetrics!.Memory.RetainedBytes.ShouldBe(0);
        session.LastStatementMetrics.Memory.ActiveLeases.ShouldBe(0);
        (await ValueAsync(session, "MATCH (n:Unwritten) RETURN count(*)")).ShouldBe(0L);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Collection shapes: entity groups, nested maps and path lists retain scalar projections")]
    public async Task ExecuteAsync_EntityLists_ShouldPropagateElementShapesAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("list-entities", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:N {k:1})-[:T {w:2}]->(:N {k:2})-[:T {w:3}]->(:N {k:3})", cancellationToken: CancellationToken.None);
        // Act / Assert
        foreach (string query in new[]
        {
            "MATCH (a:N {k:1})-[rs:T]->{2}(b) RETURN [r IN rs | r.w]",
            "MATCH (a:N {k:1}) ((x)-[rs:T]->(y)){2} (b) RETURN [r IN rs | r.w]",
            "MATCH p=(a:N {k:1})-[:T]->{2}(b) RETURN [n IN nodes(p)[1..] | n.k]",
            "MATCH (a:N {k:1})-[rs:T]->{2}(b) WITH [r IN rs | {r:r}] AS ms FOR m IN ms RETURN collect((m.r).w)",
        })
        {
            (await ValueAsync(session, query)).ShouldBeOfType<object?[]>().ShouldBe(new object?[] { 2L, 3L });
        }
        (await ValueAsync(session, "MATCH (a:N {k:1}) WHERE [x IN [a] | x.k][0] = 1 RETURN a.k")).ShouldBe(1L);
        (await ValueAsync(session, "MATCH (a:N {k:1})-[rs:T]->{2}(b) RETURN [r IN rs WHERE r:T | r.w]")).ShouldBeOfType<object?[]>().ShouldBe(new object?[] { 2L, 3L });
        (await ValueAsync(session, "MATCH (a:N {k:1})-[rs:T]->{2}(b) WITH [r IN rs | r.w] AS ws RETURN ws NEXT RETURN [w IN ws[1..] | w]")).ShouldBeOfType<object?[]>().ShouldBe(new object?[] { 3L });
        var explain = await session.ExecuteAsync("EXPLAIN MATCH (a)-[rs]->{2}(b) RETURN [r IN rs | r.w]", cancellationToken: CancellationToken.None);
        await using (var rows = explain.ShouldBeOfType<DatabasePlanDescription>()) { rows.Columns.ShouldNotBeEmpty(); }
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Slices: copying consumes work and releases all grants on refusal")]
    public async Task ExecuteAsync_SliceLimit_ShouldConsumeStatementWorkAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("slice-budget", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var request = GraphQueryRequest.FromGql("RETURN $xs[0..]", new Dictionary<string, object?> { ["xs"] = Enumerable.Range(0, 100).ToArray() }, new() { WorkUnits = 50 });
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(request, CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05004");
        session.LastStatementMetrics!.Physical.ShouldBe(51);
        session.LastStatementMetrics.Memory.RetainedBytes.ShouldBe(0);
        session.LastStatementMetrics.Memory.ActiveLeases.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Slices: temporary sources remain budgeted while bounds evaluate nested lists")]
    public async Task ExecuteAsync_NestedSliceBound_ShouldRetainSourceGrantAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("slice-bound-budget", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var parameters = new Dictionary<string, object?>
        { ["xs"] = Enumerable.Range(0, 50).ToArray(), ["ys"] = Enumerable.Range(100, 50).ToArray() };
        // Each comprehension fits alone. The source and the bound's temporary list coexist.
        await using (var source = (DatabaseResultSet)await session.ExecuteAsync(GraphQueryRequest.FromGql(
            "RETURN [x IN $xs | x]", parameters, new() { MemoryBytes = 12_288 }), CancellationToken.None)) { }
        var request = GraphQueryRequest.FromGql("RETURN [x IN $xs | x][size([y IN $ys | y])..]", parameters, new() { MemoryBytes = 12_288 });
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(request, CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05020");
        session.LastStatementMetrics!.Memory.RetainedBytes.ShouldBe(0);
        session.LastStatementMetrics.Memory.ActiveLeases.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Collections: malformed public iterator ASTs report binding diagnostics")]
    public void Plan_MissingIteratorName_ShouldReportBindingDiagnostic()
    {
        // Arrange
        var expression = new GqlListComprehensionExpression("", new GqlListExpression([]));
        // Act / Assert
        Should.Throw<GraphPlanningException>(() => GraphValueExpressions.Children(expression).ToArray()).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG01017");
    }

    private static async Task<object?> ValueAsync(GraphDatabaseSession session, string query)
    {
        await using var rows = (DatabaseResultSet)await session.ExecuteAsync(query, cancellationToken: CancellationToken.None);
        var output = new List<object?>();
        await foreach (var row in rows.GetRowsAsync(CancellationToken.None)) { output.Add(row.GetValue(0)); }
        return output.ShouldHaveSingleItem();
    }
}
