using System;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Graph.Catalog;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Client.Tests;

public sealed class GraphConstraintWireTests
{
    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Constraints: typed schema metadata and enforcement cross InMemory and TCP")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_TypedConstraints_ShouldListEnforceDropAndReuseSessionAsync(bool tcp)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync(tcp);
        await using var local = await harness.Database.CreateSessionAsync(harness.Token);
        var schema = GraphSchema.Open(harness.Database, local);
        Guid label = Guid.NewGuid(), type = Guid.NewGuid();
        await schema.SaveLabelAsync(new(label, "N"), harness.Token);
        await schema.SaveRelationshipTypeAsync(new(type, "T"), harness.Token);
        await schema.CreateConstraintAsync(new(label, "node_type", "p", DatabaseType.Int64), harness.Token);
        await schema.CreateConstraintAsync(new(label, "node_exists", "p"), harness.Token);
        await schema.CreateConstraintAsync(new(type, "edge_exists", "p"), harness.Token);
        await schema.CreateIndexAsync("N", "index", "p", harness.Token);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        Guid session = harness.Server.Sessions.ShouldHaveSingleItem().Id;

        // Act / Assert
        var properties = await connection.QueryAsync("SHOW PROPERTY KEYS", cancellationToken: harness.Token);
        properties.Select(row => row.ToArray()).ToArray().ShouldBe(new object?[][]
        {
            ["graph", "LABEL", label, "N", "p", "Int64", true],
            ["graph", "RELATIONSHIP TYPE", type, "T", "p", null, true],
        });
        var ownership = await connection.QueryAsync("SHOW OBJECT OWNERSHIP", cancellationToken: harness.Token);
        ownership.Where(row => Equals(row[4], "CONSTRAINT")).Select(row => row[5]).ShouldBe(new object?[] { "node_exists", "node_type", "edge_exists" });
        (await connection.QueryAsync("SHOW INDEXES", cancellationToken: harness.Token)).ShouldHaveSingleItem()[3].ShouldBe("index");
        foreach (string query in new[] { "INSERT (:N)", "INSERT (:N {p:'bad'})", "INSERT ()-[:T]->()" })
        {
            var failure = await Should.ThrowAsync<GraphClientException>(async () => await connection.ExecuteAsync(query, cancellationToken: harness.Token));
            failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG03003");
            harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(session);
        }
        await connection.ExecuteAsync("INSERT (a:N {p:1})-[:T {p:TRUE}]->(a)", cancellationToken: harness.Token);
        await schema.DropConstraintAsync("node_exists", harness.Token);
        await connection.ExecuteAsync("MATCH (n:N) REMOVE n.p", cancellationToken: harness.Token);
        var remaining = await connection.QueryAsync("SHOW PROPERTY KEYS", cancellationToken: harness.Token);
        remaining[0][5].ShouldBe("Int64");
        remaining[0][6].ShouldBe(false);
        await schema.DropIndexAsync("index", harness.Token);
        (await connection.QueryAsync("SHOW INDEXES", cancellationToken: harness.Token)).ShouldBeEmpty();
        await connection.ExecuteAsync("MATCH (n:N) DETACH DELETE n", cancellationToken: harness.Token);
        (await connection.QueryAsync("MATCH (n) RETURN count(*)", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(0L);
        harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(session);
    }
}
