using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Client.Tests;

public sealed class GraphExistsWireTests
{
    [Theory(DisplayName = "Cohesion Test [Graph.Client] - EXISTS: correlation, anti filtering and diagnostics survive InMemory and TCP")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_Exists_ShouldPreserveSemanticsAndConnectionAsync(bool tcp)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        Guid session = harness.Server.Sessions.ShouldHaveSingleItem().Id;
        await connection.ExecuteAsync("INSERT (a:N {k:1})-[:T]->(b:N {k:2}), (:N {k:3})", cancellationToken: harness.Token);
        // Act
        var rows = await connection.QueryAsync("MATCH (a:N) RETURN a.k, EXISTS { MATCH (a)-[:T]->(b) WHERE b.k = $k } ORDER BY a.k",
            new Dictionary<string, object?> { ["k"] = 2L }, harness.Token);
        // Assert
        rows.Select(row => string.Join(',', row.ToArray())).ShouldBe(new[] { "1,True", "2,False", "3,False" });
        (await connection.QueryAsync("MATCH (a:N) WHERE NOT EXISTS { (a)->() } RETURN a.k ORDER BY a.k", cancellationToken: harness.Token))
            .Select(row => row[0]).ShouldBe(new object?[] { 2L, 3L });
        (await connection.QueryAsync("OPTIONAL MATCH (n:Missing) RETURN EXISTS { (n)->() }, NOT EXISTS { (n)->() }", cancellationToken: harness.Token))
            .ShouldHaveSingleItem().ToArray().ShouldBe(new object?[] { false, true });
        (await connection.QueryAsync("MATCH (a:N) WHERE any(x IN [a] WHERE EXISTS { (x)->() }) RETURN a.k", cancellationToken: harness.Token))
            .ShouldHaveSingleItem()[0].ShouldBe(1L);
        (await connection.QueryAsync("EXPLAIN MATCH (a:N) WHERE NOT EXISTS { (a)->() } RETURN a.k", cancellationToken: harness.Token))
            .ShouldContain(row => Equals(row[2], "AntiSemiApply"));
        foreach (var (query, code) in new[] { ("RETURN EXISTS ((a))", "CK00001"), ("RETURN EXISTS {}", "CG00013"),
            ("LET a=1 RETURN EXISTS { (a) }", "CG01019"), ("RETURN EXISTS { (a) }, a", "CG05001") })
        {
            var error = await Should.ThrowAsync<GraphClientException>(async () => await connection.QueryAsync(query, cancellationToken: harness.Token));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
        }
        (await connection.QueryAsync("RETURN EXISTS { (:N) }", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(true);
        harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(session);
    }
}
