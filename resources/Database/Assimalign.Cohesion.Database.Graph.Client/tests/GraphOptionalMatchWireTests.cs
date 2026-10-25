using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Protocol;

using Assimalign.Cohesion.Database.Graph.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Client.Tests;

public sealed class GraphOptionalMatchWireTests
{
    [Fact(DisplayName = "Cohesion Test [Graph.Client] - OPTIONAL MATCH: scalar nulls cross the wire and unknown labels retain warnings")]
    public async Task QueryAsync_NullExtension_ShouldReturnNullScalarsAsync()
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.ExecuteAsync("INSERT (a:Person {name:'Alice'})-[:KNOWS]->(:Person {name:'Bob'}), (:Person {name:'Isolated'})", cancellationToken: harness.Token);
        // Act
        var result = await connection.QueryAsync("MATCH (a:Person) OPTIONAL MATCH (a)-[:KNOWS]->(b) RETURN a.name, b.name", cancellationToken: harness.Token);
        var unknown = await connection.QueryAsync("OPTIONAL MATCH (n:Missing) RETURN n.name", cancellationToken: harness.Token);
        // Assert
        result.Count.ShouldBe(3);
        result.Select(row => (row[0], row[1]))
            .ShouldBe(new (object?, object?)[] { ("Alice", "Bob"), ("Bob", null), ("Isolated", null) }, ignoreOrder: true);
        unknown.ShouldHaveSingleItem()[0].ShouldBeNull();
        unknown.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG01010");
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Client] - OPTIONAL MATCH: path rejection completes with CG05001 and preserves the session")]
    [InlineData("OPTIONAL MATCH (a:Person) RETURN a")]
    [InlineData("MATCH (a:Person {name:'Alice'}) OPTIONAL MATCH (a)-[r:KNOWS]->(b) RETURN b")]
    [InlineData("MATCH (a:Person {name:'Alice'}) OPTIONAL MATCH (a)-[r:KNOWS]->(b) RETURN r")]
    [InlineData("MATCH (a:Person {name:'Alice'}) OPTIONAL MATCH p = (a)-[:KNOWS]->(b) RETURN p")]
    public async Task QueryPathsAsync_OptionalOnlyProjection_ShouldRejectAndKeepSessionReadyAsync(string source)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.ExecuteAsync("INSERT (:Person {name:'Alice'})-[:KNOWS]->(:Person {name:'Bob'})", cancellationToken: harness.Token);
        Guid id = harness.Server.Sessions.ShouldHaveSingleItem().Id;
        // Act
        var error = await Should.ThrowAsync<GraphClientException>(async () =>
        { await foreach (var path in connection.QueryPathsAsync(source, cancellationToken: harness.Token)) { } });
        // Assert
        error.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05001");
        (await connection.QueryAsync("RETURN 1 AS one", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(1L);
        harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(id);
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Client] - OPTIONAL MATCH: mandatory paths carry optional warnings at completion")]
    public async Task QueryPathsAsync_MandatoryProjection_ShouldKeepPathsAndWarningsAsync()
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.ExecuteAsync("INSERT (:Person {name:'Alice'})", cancellationToken: harness.Token);
        var result = connection.QueryPathsAsync("MATCH (a:Person) OPTIONAL MATCH (b:Missing) RETURN a", cancellationToken: harness.Token);
        var paths = new List<GraphPath>();
        // Act
        await foreach (var path in result) { paths.Add(path); }
        // Assert
        paths.ShouldHaveSingleItem().Nodes.ShouldHaveSingleItem().Properties["name"].ShouldBe("Alice");
        result.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG01010");
    }
}
