using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;
using Assimalign.Cohesion.Database.Graph;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphUnionFixTests
{
    [Theory(DisplayName = "Cohesion Test [Graph] - UNION: NEXT consumes the combined binding table")]
    [InlineData("RETURN 1 AS x UNION ALL RETURN 2 AS x NEXT RETURN x*10 AS x", "10;20")]
    [InlineData("RETURN 1 AS x UNION ALL RETURN 2 AS x NEXT RETURN sum(x) AS x", "3")]
    [InlineData("RETURN 1 AS x UNION ALL RETURN 2 AS x NEXT RETURN x AS x ORDER BY x DESC SKIP 1 LIMIT 1", "1")]
    [InlineData("(RETURN 1 AS x) UNION ALL (RETURN 2 AS x) NEXT (RETURN x*10 AS x)", "10;20")]
    [InlineData("RETURN 1 AS x UNION ALL RETURN 2 AS x NEXT RETURN x*10 AS x NEXT RETURN x+1 AS x", "11;21")]
    [InlineData("RETURN 1 AS x UNION ALL RETURN 2 AS x NEXT RETURN x+1 AS y UNION ALL RETURN x+2 AS y", "2;3;3;4")]
    [InlineData("(RETURN 1 AS x NEXT RETURN x+1 AS x) UNION ALL (RETURN 2 AS x NEXT RETURN x+2 AS x) NEXT RETURN x*10 AS x", "20;40")]
    public async Task ExecuteAsync_NextOverComposite_ShouldRespectPrecedenceAsync(string query, string expected)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("union-next-fix", new());
        var database = await engine.CreateDatabaseAsync("g", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var rows = await GraphExistsTests.RowsAsync(session, query);
        var explain = await GraphExistsTests.RowsAsync(session, "EXPLAIN " + query);
        // Assert
        string.Join(';', rows.Select(row => GraphExistsTests.Text(row[0]))).ShouldBe(expected);
        explain[0][2].ShouldBe("Pipeline");
        explain.ShouldContain(row => Equals(row[2], "UnionAll"));
        explain.ShouldContain(row => Equals(row[2], "Next"));
        session.LastStatementMetrics!.Physical.ShouldBe(0);
        session.LastStatementMetrics.Memory.RetainedBytes.ShouldBe(0);
        session.LastStatementMetrics.Memory.ActiveLeases.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - UNION: NEXT preserves entity shapes and correlated registries")]
    public async Task ExecuteAsync_CorrelationsAfterComposite_ShouldUseCombinedBindingsAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("union-next-correlated", new());
        var database = await engine.CreateDatabaseAsync("g", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(GraphExistsTests.Seed, cancellationToken: CancellationToken.None);
        // Act
        var rows = await GraphExistsTests.RowsAsync(session,
            "MATCH (a:N {k:1}) RETURN a AS n UNION ALL MATCH (a:N {k:2}) RETURN a AS n " +
            "NEXT RETURN n.k AS k,EXISTS { (n)->(m) } AS e,[(n)->(m) | m.k] AS xs ORDER BY k");
        // Assert
        rows.Select(row => row[0]).ShouldBe(new object?[] { 1L, 2L });
        rows.Select(row => row[1]).ShouldBe(new object?[] { true, true });
        rows[0][2].ShouldBeOfType<object?[]>().ShouldBe(new object?[] { 2L, 2L });
        rows[1][2].ShouldBeOfType<object?[]>().ShouldBe(new object?[] { 3L });
        session.LastStatementMetrics!.Memory.RetainedBytes.ShouldBe(0);
        session.LastStatementMetrics.Memory.ActiveLeases.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - UNION: NEXT retains neutral and mixed list-element proofs")]
    public async Task ExecuteAsync_ListShapesAcrossNext_ShouldRetainOnlyProvenEntityKindsAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("union-next-shapes", new());
        var database = await engine.CreateDatabaseAsync("g", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:N {k:1})", cancellationToken: CancellationToken.None);
        // Act / Assert
        foreach (string first in new[] { "[]", "[NULL]" })
        {
            var rows = await GraphExistsTests.RowsAsync(session, "RETURN " + first + " AS xs UNION ALL MATCH (n:N) RETURN [n] AS xs NEXT FOR v IN xs MATCH (v) RETURN v.k AS k");
            rows.ShouldHaveSingleItem()[0].ShouldBe(1L);
        }
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            "RETURN [1] AS xs UNION ALL MATCH (n:N) RETURN [n] AS xs NEXT FOR v IN xs MATCH (v) RETURN v.k AS k", cancellationToken: CancellationToken.None));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG03003");
        session.LastStatementMetrics!.Physical.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - UNION: NEXT retains named path proofs through later composites")]
    public async Task ExecuteAsync_PathsAcrossNext_ShouldRetainPathProjectionAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("union-next-paths", new());
        var database = await engine.CreateDatabaseAsync("g", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:N)-[:T]->(:N)", cancellationToken: CancellationToken.None);
        const string source = "MATCH p=()-[:T]->() RETURN p AS x UNION ALL MATCH p=()-[:T]->() RETURN p AS x NEXT ";
        // Act / Assert
        foreach (var (tail, count) in new[] { ("RETURN x LIMIT 1", 1), ("RETURN x UNION ALL RETURN x", 4) })
        {
            var result = (GraphPathsQueryResult)await session.ExecuteAsync(GraphPathsQueryRequest.FromGql(source + tail), CancellationToken.None);
            result.Paths.Count.ShouldBe(count);
            result.Paths.ShouldAllBe(path => path.Relationships.Count == 1);
        }
        session.LastStatementMetrics!.Memory.RetainedBytes.ShouldBe(0);
        session.LastStatementMetrics.Memory.ActiveLeases.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - UNION: grouped path selectors retain MATCH context")]
    [InlineData("ANY")]
    [InlineData("ALL")]
    [InlineData("ANY SHORTEST")]
    [InlineData("ALL SHORTEST")]
    [InlineData("SHORTEST 2")]
    [InlineData("SHORTEST 2 GROUPS")]
    public async Task ExecuteAsync_GroupedSelector_ShouldMatchUngroupedAsync(string selector)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("union-selector-fix", new());
        var database = await engine.CreateDatabaseAsync("g", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:N)-[:T]->(:N)", cancellationToken: CancellationToken.None);
        string operand = "MATCH p=" + selector + " (n:N)-[:T]->(m) RETURN count(*) AS x";
        // Act / Assert
        foreach (int depth in new[] { 1, 3 })
        {
            var rows = await GraphExistsTests.RowsAsync(session, "RETURN 0 AS x UNION ALL " + new string('(', depth) + operand + new string(')', depth));
            rows.Select(row => row[0]).ShouldBe(new object?[] { 0L, 1L });
        }
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - UNION DISTINCT: explicit and implicit distinct modes compose")]
    [InlineData("RETURN 1 AS x UNION DISTINCT RETURN 1.0 AS x UNION RETURN 2 AS x", "1;2", null)]
    [InlineData("RETURN 1 AS x UNION DISTINCT RETURN 2 AS x UNION ALL RETURN 3 AS x", null, "CG00016")]
    public async Task ExecuteAsync_ExplicitDistinct_ShouldUseUnionModeAsync(string query, string? expected, string? code)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("union-distinct-fix", new());
        var database = await engine.CreateDatabaseAsync("g", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act / Assert
        if (code is not null)
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(query, cancellationToken: CancellationToken.None));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
        }
        else
        {
            var rows = await GraphExistsTests.RowsAsync(session, query);
            string.Join(';', rows.Select(row => GraphExistsTests.Text(row[0]))).ShouldBe(expected);
            rows[0][0].ShouldBeOfType<long>();
        }
    }
}
