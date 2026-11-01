using System;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Client.Tests;

public sealed class GraphUnionWireTests
{
    [Theory(DisplayName = "Cohesion Test [Graph.Client] - UNION: values, metadata, EXPLAIN, refusals and session reuse cross both transports")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_CompositeQueries_ShouldRoundTripAndPreserveSessionAsync(bool tcp)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.ExecuteAsync("INSERT (a:N {k:1})-[:T]->(:N {k:2})", cancellationToken: harness.Token);
        Guid session = harness.Server.Sessions.ShouldHaveSingleItem().Id;
        // Act / Assert
        var distinct = await connection.QueryAsync("FOR x IN [1,1,NULL] RETURN x UNION FOR x IN [NULL,2,2] RETURN x", cancellationToken: harness.Token);
        distinct.Select(row => row[0]).ShouldBe(new object?[] { 1L, null, 2L });
        var all = await connection.QueryAsync("FOR x IN [1,NULL] RETURN x UNION ALL FOR x IN [NULL,1] RETURN x", cancellationToken: harness.Token);
        all.Select(row => row[0]).ShouldBe(new object?[] { 1L, null, null, 1L });
        var composite = await connection.QueryAsync("MATCH (n:N {k:1}) WHERE EXISTS { (n)->(m) } RETURN [(n)->(m) | m.k] AS x UNION RETURN [2] AS x", cancellationToken: harness.Token);
        composite.ShouldHaveSingleItem()[0].ShouldBeOfType<object?[]>().ShouldBe(new object?[] { 2L });
        var explain = await connection.QueryAsync("EXPLAIN RETURN 1 AS x UNION (RETURN 2 AS x UNION RETURN 3 AS x)", cancellationToken: harness.Token);
        explain.Count(row => Equals(row[2], "Union")).ShouldBe(2);
        foreach (var (query, code) in new[]
        {
            ("RETURN 1 AS x UNION INSERT (:Forbidden) RETURN 1 AS x", "CG05029"),
            ("RETURN 1 AS x UNION ALL RETURN 2 AS x UNION RETURN 3 AS x", "CG00016"),
            ("RETURN 1 AS x UNION RETURN 2 AS y", "CG01020"),
            ("RETURN 1 AS x UNION RETURN 'a' AS x", "CG02017"),
            ("RETURN [1/0] AS x UNION MATCH (n:N) RETURN [n] AS x", "CG05001"),
            ("RETURN [] AS x UNION MATCH (n:Missing) RETURN [n] AS x", "CG05001"),
            ("RETURN 0 AS k NEXT RETURN [] AS x UNION MATCH (n:Missing) RETURN [n] AS x", "CG05001"),
            ("RETURN [] AS x UNION MATCH (n:Missing) RETURN [n] AS x NEXT RETURN x", "CG05001"),
        })
        {
            var error = await Should.ThrowAsync<GraphClientException>(async () => await connection.QueryAsync(query, cancellationToken: harness.Token));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
            harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(session);
        }
        (await connection.QueryAsync("MATCH (n:Forbidden) RETURN count(*)", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(0L);
        var original = await connection.QueryAsync("RETURN count(*) AS c", cancellationToken: harness.Token);
        var joined = await connection.QueryAsync("RETURN count(*) AS c UNION ALL RETURN CAST(2 AS FLOAT64) AS c", cancellationToken: harness.Token);
        joined.Columns.Select(column => column.Name).ShouldBe(original.Columns.Select(column => column.Name));
        joined.Columns.ShouldHaveSingleItem().Type.ShouldBe(DatabaseType.Float64);
        await connection.DisposeAsync();
        await using var again = await harness.Client.ConnectAsync(harness.Token);
        (await again.QueryAsync("RETURN 1 AS x UNION ALL RETURN 2 AS x", cancellationToken: harness.Token)).Count.ShouldBe(2);
        harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(session);
    }
}
