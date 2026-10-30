using System;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Client.Tests;

public sealed class GraphSetQueryWireTests
{
    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Set queries: retained-state refusal crosses both transports and preserves the session")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_SetBudget_ShouldRefuseAndReuseSessionAsync(bool tcp)
    {
        // Arrange: repeated small insert statements fit the same server grant as later reads.
        await using var harness = await GraphClientTestHarness.StartAsync(tcp, options: new() { QueryMemoryBudget = 20_000 });
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        for (int i = 0; i < 150; i++)
        { await connection.ExecuteAsync($"INSERT (:N {{k:{i}}})", cancellationToken: harness.Token); }
        Guid session = harness.Server.Sessions.ShouldHaveSingleItem().Id;
        // Act / Assert
        foreach (string op in new[] { "EXCEPT ALL", "INTERSECT ALL" })
        {
            var error = await Should.ThrowAsync<GraphClientException>(async () => await connection.QueryAsync(
                $"MATCH (n:N) RETURN n.k AS x {op} RETURN -1 AS x", cancellationToken: harness.Token));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05032");
            (await connection.QueryAsync("RETURN 42", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(42L);
            harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(session);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Composite queries: multiplicities, lazy fallback, paths, metadata and refusals cross both transports")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_SetQueries_ShouldRoundTripAndReuseSessionAsync(bool tcp)
    {
        // Arrange
        int calls = 0;
        await using var harness = await GraphClientTestHarness.StartAsync(tcp, functions =>
            functions.Add(GraphScalarFunction.Create<long, long>("fallback_probe", value =>
            { calls++; throw new InvalidOperationException("fallback sentinel"); })));
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.ExecuteAsync("INSERT (:N {k:1})-[:T]->(:N {k:2})", cancellationToken: harness.Token);
        Guid session = harness.Server.Sessions.ShouldHaveSingleItem().Id;
        // Act / Assert
        foreach (var (op, expected) in new[]
        {
            ("EXCEPT", new object?[] { 2L }), ("EXCEPT ALL", new object?[] { 1L, 2L, null }),
            ("INTERSECT", new object?[] { 1L, null }), ("INTERSECT ALL", new object?[] { 1L, null }),
        })
        {
            var rows = await connection.QueryAsync($"FOR x IN [1,1,2,NULL,NULL] RETURN x {op} FOR x IN [1,NULL] RETURN x", cancellationToken: harness.Token);
            rows.Select(row => row[0]).ShouldBe(expected);
            var explain = await connection.QueryAsync($"EXPLAIN RETURN 1 AS x {op} RETURN 2 AS x", cancellationToken: harness.Token);
            ((string)explain[0][5]!).ShouldContain("CG05032", Case.Sensitive);
        }
        var chosen = await connection.QueryAsync("FOR x IN [1,1,NULL] RETURN x OTHERWISE RETURN fallback_probe(9) AS x", cancellationToken: harness.Token);
        chosen.Select(row => row[0]).ShouldBe(new object?[] { 1L, 1L, null });
        calls.ShouldBe(0);
        var fallback = await connection.QueryAsync("FOR x IN [] RETURN x OTHERWISE FOR x IN [7,7] RETURN x NEXT RETURN x+1 AS x", cancellationToken: harness.Token);
        fallback.Select(row => row[0]).ShouldBe(new object?[] { 8L, 8L });
        var header = await connection.QueryAsync("RETURN count(*) AS x OTHERWISE RETURN CAST(2 AS FLOAT64) AS x", cancellationToken: harness.Token);
        header.Columns.ShouldHaveSingleItem().Type.ShouldBe(DatabaseType.Float64);
        var composite = await connection.QueryAsync("RETURN {a:[1,NULL]} AS x INTERSECT RETURN {a:[1.0,NULL]} AS x", cancellationToken: harness.Token);
        composite.Count.ShouldBe(1);
        foreach (string op in new[] { "INTERSECT ALL", "OTHERWISE", "EXCEPT ALL" })
        {
            int paths = 0;
            var result = connection.QueryPathsAsync("MATCH p=()-[:T]->() FOR x IN [1,2] RETURN p " + op + " MATCH p=()-[:T]->() RETURN p", cancellationToken: harness.Token);
            await foreach (var path in result) { path.Relationships.Count.ShouldBe(1); paths++; }
            paths.ShouldBe(op == "OTHERWISE" ? 2 : 1);
        }
        foreach (var (query, code) in new[]
        {
            ("RETURN 1 AS x OTHERWISE INSERT (:Forbidden) RETURN 1 AS x", "CG05029"),
            ("RETURN 1 AS x OTHERWISE RETURN 2 AS y", "CG01020"),
            ("RETURN 1 AS x EXCEPT RETURN 'a' AS x", "CG02017"),
            ("RETURN 1 AS x OTHERWISE RETURN $missing AS x", "CG05001"),
            ("RETURN [] AS x OTHERWISE MATCH (n:Missing) RETURN [n] AS x", "CG05001"),
            ("RETURN [] AS x EXCEPT MATCH (n:Missing) RETURN [n] AS x", "CG05001"),
            ("RETURN [] AS x INTERSECT MATCH (n:Missing) RETURN [n] AS x", "CG05001"),
            ("RETURN fallback_probe(1) AS x OTHERWISE RETURN fallback_probe(2) AS x", "CG05021"),
        })
        {
            var error = await Should.ThrowAsync<GraphClientException>(async () => await connection.QueryAsync(query, cancellationToken: harness.Token));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
            harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(session);
        }
        calls.ShouldBe(1);
        (await connection.QueryAsync("MATCH (n:Forbidden) RETURN count(*)", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(0L);
        await connection.DisposeAsync();
        await using var again = await harness.Client.ConnectAsync(harness.Token);
        (await again.QueryAsync("RETURN 1 AS x INTERSECT RETURN 1 AS x", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(1L);
        harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(session);
    }
}
