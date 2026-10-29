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

public sealed class GraphPatternComprehensionTests
{
    public static IEnumerable<object[]> PatternCases =>
    [
        ["MATCH (a:N) RETURN a.k, [(a)-[:T]->(b) | b.k] ORDER BY a.k", "1,2,2;2,3;3,;4,"],
        ["RETURN [(:N {k:2})-[:T]->(b) | b.k]", "3"],
        ["RETURN [(:Missing)->(b) | b.k]", ""],
        ["RETURN [p=(:N {k:1})-[r:T]->(b) WHERE b.k=2 | size(relationships(p)) + size(nodes(p))]", "3,3"],
        ["RETURN [(:N {k:1})-[r:T]->(b) WHERE FALSE | b.k]", ""],
        ["RETURN [(:N {k:1})-[r:T]->(b) | NULL]", "NULL,NULL"],
        ["OPTIONAL MATCH (a:Missing) RETURN [(a)->(b) | b.k]", ""],
        ["LET a=NULL RETURN [(a)->(b) | b.k]", ""],
        ["LET p=NULL RETURN [p=(:N {k:1})->(b) | size(nodes(p))]", "2,2"],
        ["MATCH (a:N) RETURN [x IN [a] | size([(x)->(b) | b.k])] ORDER BY a.k", "2;1;0;0"],
        ["MATCH (a:N) WHERE size([(a)->(b) | b.k]) > 0 RETURN a.k ORDER BY a.k", "1;2"],
        ["MATCH (a:N) WHERE any(x IN [a] WHERE size([(x)->(b) | b.k])=2) RETURN a.k", "1"],
        ["MATCH (a:N) RETURN reduce(s=0, x IN [a] | s + size([(x)->(b) | b.k])) ORDER BY a.k", "2;1;0;0"],
        ["RETURN [(a:N {k:1})->(b) | [(b)->(c) | c.k]]", "3,3"],
        ["MATCH (a:N) RETURN sum(size([(a)->(b) | b.k]))", "3"],
        ["MATCH (a:N) RETURN collect_list([(a)->(b) | b.k])", "2,2,3,,"],
        ["MATCH (a:N) SET a.out = size([(a)->(b) | b.k]) RETURN a.out ORDER BY a.k", "2;1;0;0"],
        ["RETURN size([p=(a)-[:T*0..0]->(b) | size(nodes(p))])", "4"],
        ["MATCH (a:N) RETURN count(*) + size([a=(:N {k:2})->(b) | b.k])", "5"],
        ["RETURN EXISTS { (a:N {k:1}) WHERE size([(a)->(b) | b.k])=2 }", "True"],
        ["MATCH (a:N) RETURN a.k AS k ORDER BY size([(b:N {k:k})->(c) | c.k]), k", "3;4;2;1"],
        ["RETURN size([(:N {k:1})->(b) | b][..1])", "1"],
        ["MATCH (a:N {k:1}) RETURN reduce(s=[],x IN [a] | [(x)->(b) | b.k])", "2,2"],
        ["MATCH (a:N {k:1}) RETURN reduce(s=0,x IN [a,a] | [(x)->(b) | s])", "0,0,0,0"],
        ["RETURN [()-[rs:T*1..2]->() | [r IN rs | type(r)]]", "T,T,T,T,T,T,T"],
        ["OPTIONAL MATCH ()-[r:Missing]->() RETURN [()-[r]->(b) | b.k]", ""],
        ["FOR b IN [(:N {k:1})->(b) | b] RETURN b.k", "2;2"],
    ];

    [Theory(DisplayName = "Cohesion Test [Graph] - Pattern comprehensions: collect ordered correlated projections in every expression position")]
    [MemberData(nameof(PatternCases))]
    public async Task ExecuteAsync_PatternCollection_ShouldReturnExpectedRowsAsync(string query, string expected)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("pattern-values", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(GraphExistsTests.Seed, cancellationToken: CancellationToken.None);
        // Act
        var rows = await GraphExistsTests.RowsAsync(session, query);
        // Assert
        string.Join(';', rows.Select(row => string.Join(',', row.Select(GraphExistsTests.Text)))).ShouldBe(expected);
        session.LastStatementMetrics!.Memory.RetainedBytes.ShouldBe(0);
        session.LastStatementMetrics.Memory.ActiveLeases.ShouldBe(0);
    }

    public static IEnumerable<object[]> QuantifiedRelationshipCases
    {
        get
        {
            foreach (string edge in new[] { "->", "<-", "-", "<->", "-[r]->", "<-[r]-", "-[r]-", "<-[r]->" })
            {
                bool directed = edge is "->" or "<-" or "-[r]->" or "<-[r]-";
                foreach (var (suffix, directedCount, eitherCount) in new (string, long, long)[]
                { ("{1,2}", 5, 14), ("{2}", 2, 8), ("{1,}", 5, 18), ("{,2}", 9, 18), ("{,}", 9, 22), ("+", 5, 18), ("*", 9, 22) })
                { yield return [edge, suffix, directed ? directedCount : eitherCount]; }
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Pattern comprehensions: quantified relationships collect the same paths as MATCH (#1736)")]
    [MemberData(nameof(QuantifiedRelationshipCases))]
    public async Task ExecuteAsync_QuantifiedPatternCollection_ShouldMatchCountAsync(string edge, string quantifier, long expected)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("pattern-quantified", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(GraphExistsTests.Seed, cancellationToken: CancellationToken.None);
        string pattern = "(a)" + edge + quantifier + "(b)";
        var match = await GraphExistsTests.RowsAsync(session, "MATCH p=" + pattern + " RETURN count(*)");
        match.ShouldHaveSingleItem().ShouldHaveSingleItem().ShouldBe(expected);
        // Act
        var rows = await GraphExistsTests.RowsAsync(session, "RETURN size([p=" + pattern + " | p]), size([" + pattern + " | b])");
        // Assert
        rows.ShouldHaveSingleItem().ShouldBe(new object?[] { expected, expected });
        session.LastStatementMetrics!.Memory.RetainedBytes.ShouldBe(0);
        session.LastStatementMetrics.Memory.ActiveLeases.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Pattern comprehensions: shared AST binds separately in each occurrence")]
    public async Task ExecuteAsync_SharedPatternAst_ShouldBindPerOccurrenceAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("pattern-dag", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(GraphExistsTests.Seed, cancellationToken: CancellationToken.None);
        var shared = GraphQueryRequest.FromGql("RETURN [(x:N)->(b) | b.k]").Statement.GqlExpression.Projections[0].Expression;
        var query = new GqlQueryExpression([], null, [], [], false,
            [new GqlProjection(shared, "standalone"), new GqlProjection(new GqlListComprehensionExpression("x",
                new GqlListExpression([new GqlLiteralExpression(null)]), projection: shared), "scoped")]);
        // Act
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(new GraphQueryRequest(new GqlQueryStatement(query)), CancellationToken.None);
        // Assert
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            row.GetValue(0).ShouldBeOfType<object?[]>().ShouldBe(new object?[] { 2L,2L,3L });
            row.GetValue(1).ShouldBeOfType<object?[]>().ShouldHaveSingleItem().ShouldBeOfType<object?[]>().ShouldBeEmpty();
        }
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Pattern comprehensions: planning validates every lexical scope before execution")]
    [InlineData("RETURN [(a)->(b) | b.k], b", "CG05001")]
    [InlineData("LET a=1 RETURN [(a)->(b) | b.k]", "CG01019")]
    [InlineData("RETURN [(a)->(b) | absent]", "CG05001")]
    [InlineData("RETURN [(a)->(b) | count(*)]", "CG05028")]
    [InlineData("RETURN [(a)->(b) WHERE 1 | b.k]", "CG02013")]
    [InlineData("MATCH (a:N) RETURN count(*) + size([(a)->(b) | b.k])", "CG05001")]
    public async Task ExecuteAsync_InvalidPatternCollection_ShouldReportCodeAsync(string query, string code)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("pattern-invalid", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act / Assert
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(query, cancellationToken: CancellationToken.None));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
    }
}
