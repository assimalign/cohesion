using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Client.Tests;

public sealed class GraphCallSubqueryWireTests
{
    [Theory(DisplayName = "Cohesion Test [Graph.Client] - CALL results, optional NULLs and scoped errors round-trip on one session")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_Call_ShouldRoundTripAndReuseSession(bool tcp)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        var id = harness.Server.Sessions.ShouldHaveSingleItem().Id;
        await connection.ExecuteAsync("INSERT (:N {k:1}),(:N {k:2})", cancellationToken: harness.Token);
        // Act / Assert
        var result = await connection.QueryAsync("FOR x IN [1,3] OPTIONAL CALL (x) { MATCH (n:N {k:x}) RETURN n.k AS y } RETURN x,y ORDER BY x", cancellationToken: harness.Token);
        result.Count.ShouldBe(2); result[0].ToArray().ShouldBe(new object?[] { 1L, 1L });
        result[1].ToArray().ShouldBe(new object?[] { 3L, null });
        (await connection.QueryAsync("CALL () { RETURN 1 AS y UNION RETURN 2 AS y } CALL (y) { RETURN y+1 AS z } RETURN z ORDER BY z", cancellationToken: harness.Token))
            .Select(row => row[0]).ShouldBe(new object?[] { 2L, 3L });
        var paths = 0;
        await foreach (var path in connection.QueryPathsAsync("CALL () { MATCH (n:N) RETURN n AS entity } RETURN entity", cancellationToken: harness.Token))
        { path.Nodes.ShouldHaveSingleItem(); paths++; }
        paths.ShouldBe(2);
        foreach (var item in new[] {
            ("LET x=1 CALL (x) { RETURN x } RETURN x", "CG01024"),
            ("LET x=1 CALL () { RETURN x AS y } RETURN y", "CG05001"),
            ("CALL { INSERT (:N) RETURN 1 AS y } RETURN y", "CK00001"),
            ("CALL { MATCH (n) } RETURN 1", "CK00001"),
            ("CALL proc() YIELD y RETURN y", "CK00001"),
            ("CALL { RETURN 1 AS y } IN TRANSACTIONS RETURN y", "CK00001"),
        })
        {
            var error = await Should.ThrowAsync<GraphClientException>(async () => await connection.QueryAsync(item.Item1, cancellationToken: harness.Token));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(item.Item2);
            (await connection.QueryAsync("RETURN 7", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(7L);
            harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(id);
        }
        (await connection.QueryAsync("MATCH (n:N) RETURN count(*)", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(2L);
    }
}
