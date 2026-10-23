using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;
using Assimalign.Cohesion.Database.Execution;

using Assimalign.Cohesion.Database.Graph.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Client.Tests;

public sealed class GraphOrderPagingWireTests
{
    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Ordering and paging: wire rows match in-process rows")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_OrderPaging_ShouldMatchInProcessAsync(bool tcp)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.ExecuteAsync("INSERT (:Person {name:'Alice', age:42}), (:Person {name:'Bob', age:17}), (:Person {name:'Isolated', age:30})", cancellationToken: harness.Token);
        const string source = "MATCH (a:Person) RETURN a.name ORDER BY a.age DESC NULLS LAST OFFSET @start LIMIT $rows";
        var parameters = new Dictionary<string, object?> { ["start"] = 1, ["rows"] = 2 };
        await using var session = await harness.Database.CreateSessionAsync(harness.Token);
        await using var local = (await session.ExecuteAsync(source, parameters, harness.Token)).ShouldBeAssignableTo<DatabaseResultSet>();
        var expected = new List<object?>();
        await foreach (var row in local.GetRowsAsync(harness.Token)) { expected.Add(row.GetValue(0)); }
        // Act
        var wire = await connection.QueryAsync(source, parameters, harness.Token);
        // Assert
        wire.Select(row => row[0]).ShouldBe(expected);
        expected.ShouldBe(["Isolated", "Bob"]);
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Paths: ordering and paging match typed in-process paths")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryPathsAsync_OrderPaging_ShouldMatchInProcessAsync(bool tcp)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.ExecuteAsync("INSERT (:Person {name:'Root'})-[:KNOWS]->(:Person {name:'Zoe'}), (:Person {name:'Root2'})-[:KNOWS]->(:Person {name:'Amy'})", cancellationToken: harness.Token);
        const string source = "MATCH p = (a)-[r]->(b) RETURN p ORDER BY b.name OFFSET @start LIMIT $rows";
        var parameters = new Dictionary<string, object?> { ["rows"] = 1, ["start"] = 0 };
        await using var session = await harness.Database.CreateSessionAsync(harness.Token);
        var local = (await session.ExecuteAsync(GraphPathsQueryRequest.FromGql(source, parameters), harness.Token)).ShouldBeOfType<GraphPathsQueryResult>();
        // Act
        var wire = new List<GraphPath>();
        await foreach (var path in connection.QueryPathsAsync(source, parameters, harness.Token)) { wire.Add(path); }
        // Assert
        var result = wire.ShouldHaveSingleItem();
        result.Nodes.Last().Properties["name"].ShouldBe("Amy");
        result.Nodes.Select(node => node.Id).ShouldBe(local.Paths.ShouldHaveSingleItem().Nodes.Select(node => node.Id));
        result.Relationships.Select(edge => edge.Id).ShouldBe(local.Paths[0].Relationships.Select(edge => edge.Id));
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Paging errors: invalid values precede rows and preserve session reuse")]
    [InlineData(-1)]
    [InlineData(1.5)]
    [InlineData(null)]
    public async Task QueryAsync_InvalidPage_ShouldKeepDiagnosticAndSessionAsync(object? value)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        var id = harness.Server.Sessions.ShouldHaveSingleItem().Id;
        // Act
        var error = await Should.ThrowAsync<GraphClientException>(async () =>
            await connection.QueryAsync("RETURN 1 LIMIT $rows", new Dictionary<string, object?> { ["rows"] = value }, harness.Token));
        var next = await connection.QueryAsync("RETURN 1", cancellationToken: harness.Token);
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG02013");
        next.ShouldHaveSingleItem()[0].ShouldBe(1L);
        harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(id);
    }
}
