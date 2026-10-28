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

public sealed class GraphExistsTests
{
    private const string seed = "INSERT (a:N {k:1})-[:T]->(b:N {k:2}), (a)-[:T]->(b), (b)-[:T]->(c:N {k:3}), (:N {k:4})";

    [Theory(DisplayName = "Cohesion Test [Graph] - EXISTS: correlated values are Boolean in every expression position")]
    [InlineData("MATCH (a:N) RETURN a.k, EXISTS { MATCH (a)-[:T]->(b) } ORDER BY a.k", "1,True;2,True;3,False;4,False")]
    [InlineData("MATCH (a:N) WHERE NOT EXISTS { (a)-[:T]->(b) } RETURN a.k ORDER BY a.k", "3;4")]
    [InlineData("MATCH (a:N) WHERE EXISTS { (a)-[:T]->(b) WHERE b.k > a.k } RETURN a.k ORDER BY a.k", "1;2")]
    [InlineData("MATCH (a:N) RETURN a.k ORDER BY EXISTS { (a)-[:T]->() }, a.k", "3;4;1;2")]
    [InlineData("MATCH (a:N) RETURN CASE WHEN EXISTS { (a)->() } THEN a.k ELSE 0 END AS k ORDER BY k", "0;0;1;2")]
    [InlineData("MATCH (a:N) WHERE FALSE OR EXISTS { MATCH (a)<-[:T]-(b) WHERE b.k = 1 } RETURN a.k", "2")]
    [InlineData("MATCH (a:N) WHERE NOT EXISTS { (a)->(b) WHERE NOT EXISTS { (b)->(c) WHERE c.k > a.k } } RETURN a.k ORDER BY a.k", "1;3;4")]
    [InlineData("MATCH (a:N) RETURN [x IN [a] WHERE EXISTS { (x)->() } | x.k] AS ks ORDER BY a.k", "1;2;;")]
    [InlineData("MATCH (a:N) WHERE any(x IN [a] WHERE EXISTS { (x)->() }) RETURN a.k ORDER BY a.k", "1;2")]
    [InlineData("MATCH (a:N) WHERE EXISTS { MATCH (a)->(b) MATCH (b)->(c) WHERE c.k = 3 } RETURN a.k", "1")]
    [InlineData("MATCH (a:N) LET v = a.k RETURN EXISTS { (b:N) WHERE b.k = v }", "True;True;True;True")]
    [InlineData("MATCH (a)-[r:T]->(b) WHERE EXISTS { ()-[r:T]->() WHERE b.k > a.k } RETURN a.k ORDER BY a.k", "1;1;2")]
    [InlineData("MATCH p=(a)-[:T]->(b) RETURN EXISTS { MATCH p=()-[:T]->() }", "True;True;True")]
    [InlineData("OPTIONAL MATCH (a:Missing) RETURN EXISTS { MATCH (a)->() }, NOT EXISTS { (a)->() }", "False,True")]
    [InlineData("OPTIONAL MATCH ()-[r:Missing]->() RETURN EXISTS { ()-[r]->() }", "False")]
    [InlineData("RETURN EXISTS { MATCH (:Missing) }, NOT EXISTS { (:Missing) }", "False,True")]
    [InlineData("MATCH p=(a)-[:T]->(b) FOR x IN [p] RETURN EXISTS { x=()-[:T]->() }", "True;True;True")]
    [InlineData("LET a=NULL RETURN EXISTS { (a) }, NOT EXISTS { (a) }", "False,True")]
    [InlineData("MATCH (a:N) RETURN a.k AS x ORDER BY EXISTS { (b:N) WHERE b.k = x }, x", "1;2;3;4")]
    [InlineData("MATCH (a:N) FOR copy IN [1,1] FILTER EXISTS { (a)->() } RETURN a.k ORDER BY a.k", "1;1;2;2")]
    [InlineData("MATCH (a:N WHERE EXISTS { (a)->() }) RETURN a.k ORDER BY a.k", "1;2")]
    [InlineData("MATCH (a:N {k:CASE WHEN EXISTS { (b:N) WHERE b.k=2 } THEN 1 ELSE 0 END}), (b:N {k:2}) RETURN a.k", "1")]
    [InlineData("MATCH p=(a)-[:T]->(b) RETURN EXISTS { p=()-[:T]->{1,2}() }, EXISTS { p=() }", "True,False;True,False;True,False")]
    [InlineData("MATCH (a:N) RETURN EXISTS { (a)->() } AS yes, count(*) AS c GROUP BY EXISTS { (a)->() } ORDER BY yes", "False,2;True,2")]
    [InlineData("MATCH (a:N) RETURN sum(CASE WHEN EXISTS { (a)->() } THEN 1 ELSE 0 END)", "2")]
    [InlineData("MATCH (a:N) RETURN count(*) ORDER BY EXISTS { (a:N) WHERE a.k=1 }", "4")]
    [InlineData("MATCH (a:N) RETURN DISTINCT a.k AS k ORDER BY EXISTS { (a:N) WHERE a.k=k }, k", "1;2;3;4")]
    [InlineData("MATCH (a:N) RETURN EXISTS { (a) ((m)->(n)){1} () } AS yes,count(*) AS c ORDER BY all(a IN [NULL] WHERE EXISTS { (a) ((m)->(n)){1} () })", "True,2;False,2")]
    [InlineData("MATCH (a:N) RETURN DISTINCT a.k AS k ORDER BY EXISTS { (a:N {k:1}) }, k", "1;2;3;4")]
    public async Task ExecuteAsync_CorrelatedPattern_ShouldReturnExpectedRowsAsync(string query, string expected)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("exists-values", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(seed, cancellationToken: CancellationToken.None);
        // Act
        var rows = await RowsAsync(session, query);
        // Assert
        string.Join(';', rows.Select(row => string.Join(',', row.Select(Text)))).ShouldBe(expected);
        session.LastStatementMetrics!.Memory.RetainedBytes.ShouldBe(0);
        session.LastStatementMetrics.Memory.ActiveLeases.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - EXISTS: inner names never escape and scalar pattern arguments are refused")]
    [InlineData("RETURN EXISTS { (n) }, n", "CG05001")]
    [InlineData("LET n = 1 RETURN EXISTS { (n) }", "CG01019")]
    [InlineData("RETURN EXISTS { (n) WHERE absent = 1 }", "CG05001")]
    [InlineData("RETURN FALSE AND EXISTS { (n) WHERE absent = 1 }", "CG05001")]
    [InlineData("MATCH (n:Missing) RETURN EXISTS { (n) WHERE absent = 1 }", "CG05001")]
    [InlineData("MATCH (a) RETURN count(*) + CASE WHEN EXISTS { (a) } THEN 1 ELSE 0 END", "CG05001")]
    public async Task ExecuteAsync_InvalidScope_ShouldReportDiagnosticAsync(string query, string code)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("exists-scope", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act / Assert
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(query, cancellationToken: CancellationToken.None));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - EXISTS: recommendation agrees with an independent graph oracle")]
    public async Task ExecuteAsync_Recommendations_ShouldAgreeWithBruteForceAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("exists-oracle", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var edges = new HashSet<(int, int)>();
        for (int a = 0; a < 18; a++)
        {
            await session.ExecuteAsync($"INSERT (:Person {{k:{a},id:{a}}})", cancellationToken: CancellationToken.None);
            for (int b = a + 1; b < 18; b++)
            { if ((a * 31 + b * 17) % 7 < 2) { edges.Add((a, b)); } }
        }
        foreach (var (a, b) in edges)
        { await session.ExecuteAsync($"MATCH (a:Person {{k:{a}}}), (b:Person {{k:{b}}}) INSERT (a)-[:KNOWS]->(b)", cancellationToken: CancellationToken.None); }
        bool Knows(int a, int b) => edges.Contains((Math.Min(a, b), Math.Max(a, b)));
        var expected = (from a in Enumerable.Range(0, 18) from b in Enumerable.Range(0, 18)
            where a != b && !Knows(a, b) && Enumerable.Range(0, 18).Any(f => Knows(a, f) && Knows(f, b))
            select $"{a},{b}").ToArray();
        // Act
        var rows = await RowsAsync(session, "MATCH (p:Person)-[:KNOWS]-(f)-[:KNOWS]-(c:Person) WHERE p <> c AND NOT EXISTS { MATCH (p)-[:KNOWS]-(c) } RETURN DISTINCT p.k AS p, c.k AS c ORDER BY p,c");
        // Assert
        rows.Select(row => string.Join(',', row)).ShouldBe(expected, ignoreOrder: true);
        // This is the benchmark's GQL friend-recommendation form (GRAPH.md:498).
        var benchmark = await RowsAsync(session, "MATCH (p:Person {id:1})-[:KNOWS]-(f:Person)-[:KNOWS]-(c:Person) WHERE c.id <> p.id AND NOT EXISTS { MATCH (p)-[:KNOWS]-(c) } RETURN c.id,f.id");
        benchmark.Select(row => string.Join(',', row)).ShouldBe(
            from candidate in Enumerable.Range(0, 18) from friend in Enumerable.Range(0, 18)
            where candidate != 1 && !Knows(1, candidate) && Knows(1, friend) && Knows(friend, candidate)
            select $"{candidate},{friend}", ignoreOrder: true);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - EXISTS: EXPLAIN exposes apply kind, inner seeks and correlation")]
    public async Task ExecuteAsync_Explain_ShouldDescribeSubplansAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("exists-explain", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(seed, cancellationToken: CancellationToken.None);
        // Act
        var rows = await RowsAsync(session, "EXPLAIN MATCH (a:N) WHERE NOT EXISTS { (a)-[:T]->(b) } RETURN EXISTS { (a)<-[:T]-() }");
        // Assert
        rows.ShouldContain(row => Equals(row[2], "SemiApply") && row[5]!.ToString()!.Contains("correlated=a", StringComparison.Ordinal));
        rows.ShouldContain(row => Equals(row[2], "AntiSemiApply"));
        rows.Count(row => Equals(row[2], "CorrelatedSubplan")).ShouldBe(2);
        rows.ShouldContain(row => Equals(row[2], "Expand"));
    }

    private static string Text(object? value) => value is object?[] list ? string.Join(',', list.Select(Text)) : value?.ToString() ?? "NULL";

    internal static async Task<List<object?[]>> RowsAsync(GraphDatabaseSession session, string query)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(query, cancellationToken: CancellationToken.None);
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        return rows;
    }
}
