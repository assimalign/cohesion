using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Protocol;

using Assimalign.Cohesion.Database.Graph.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Client.Tests;

public sealed class GraphReturnWireTests
{
    [Theory(DisplayName = "Cohesion Test [Graph.Client] - RETURN: DISTINCT computed columns equal in-process names and values")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_ComputedDistinct_ShouldMatchInProcessResultAsync(bool tcp)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.ExecuteAsync("INSERT (:Person {name: 'Alice', age: 42}), (:Person {name: 'Bob', age: 17})", cancellationToken: harness.Token);
        const string source = "MATCH (a:Person), (b:Person) RETURN DISTINCT a.age + $d AS next, a.name || '!' ORDER BY next DESC";
        var parameters = new Dictionary<string, object?> { ["d"] = 1 };
        await using var session = await harness.Database.CreateSessionAsync(harness.Token);
        await using var local = (await session.ExecuteAsync(source, parameters, harness.Token)).ShouldBeAssignableTo<DatabaseResultSet>();
        var expected = new List<object?[]>();
        await foreach (var row in local.GetRowsAsync(harness.Token))
        { expected.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }

        // Act
        var result = await connection.QueryAsync(source, parameters, harness.Token);

        // Assert
        result.Columns.Select(column => column.Name).ShouldBe(local.Columns.Select(column => column.Name));
        result.Columns.Select(column => column.Name).ShouldBe(["next", "a.name || '!'"]);
        result.Count.ShouldBe(2);
        for (int i = 0; i < result.Count; i++) { result[i].ToArray().ShouldBe(expected[i]); }
        result[0].ToArray().ShouldBe(new object?[] { 43L, "Alice!" });
        result[1].ToArray().ShouldBe(new object?[] { 18L, "Bob!" });
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Client] - RETURN: standalone expressions and scalar kinds pass Execute")]
    public async Task QueryAsync_ScalarKinds_ShouldComputeAndKeepParameterTypeAsync()
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        var parameters = new Dictionary<string, object?> { ["p"] = 5 };
        await connection.ExecuteAsync("INSERT (:Person {age: 42})", cancellationToken: harness.Token);

        // Act
        var standalone = await connection.QueryAsync("RETURN 1 + 1 AS two, $p AS p", parameters, harness.Token);
        var computed = await connection.QueryAsync("MATCH (a) RETURN a.age + 1, a IS NULL", cancellationToken: harness.Token);
        var projected = await connection.QueryAsync("MATCH (a) RETURN $p", parameters, harness.Token);

        // Assert
        standalone.Columns.Select(column => column.Name).ShouldBe(["two", "p"]);
        standalone.ShouldHaveSingleItem().ToArray().ShouldBe(new object?[] { 2L, 5 });
        standalone[0][1].ShouldBeOfType<int>();
        computed.Columns.Select(column => column.Name).ShouldBe(["a.age + 1", "a IS NULL"]);
        computed.ShouldHaveSingleItem().ToArray().ShouldBe(new object?[] { 43L, false });
        projected.ShouldHaveSingleItem()[0].ShouldBeOfType<int>().ShouldBe(5);
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Paths: DISTINCT and single-binding star retain GraphPathResult completion")]
    [InlineData("MATCH (a:Person), (b:Person) RETURN DISTINCT b")]
    [InlineData("MATCH (a:Person) RETURN *")]
    public async Task QueryPathsAsync_DistinctOrStar_ShouldReturnCompletePathsAsync(string source)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.ExecuteAsync("INSERT (:Person {name: 'Alice'}), (:Person {name: 'Bob'})", cancellationToken: harness.Token);
        Guid sessionId = harness.Server.Sessions.ShouldHaveSingleItem().Id;

        // Act
        GraphPathResult result = connection.QueryPathsAsync(source, cancellationToken: harness.Token);
        var paths = new List<GraphPath>();
        await foreach (var path in result) { paths.Add(path); }

        // Assert
        paths.Select(path => path.Nodes.ShouldHaveSingleItem().Properties["name"]).ShouldBe(["Alice", "Bob"]);
        result.Diagnostics.ShouldBeEmpty();
        (await connection.QueryAsync("RETURN 1 AS one", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(1L);
        harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(sessionId);
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Paths: complete projection rejection preserves the connection")]
    [InlineData("MATCH (a) RETURN a.age + 1")]
    [InlineData("MATCH (a) RETURN a, a")]
    [InlineData("MATCH (a) RETURN a AS x, a AS x")]
    [InlineData("MATCH (a) RETURN a.x, a.x")]
    [InlineData("INSERT (:Unwritten) RETURN 1 AS x, 2 AS x")]
    [InlineData("MATCH (a)-[r]->(b) RETURN *")]
    public async Task QueryPathsAsync_InvalidShape_ShouldRejectAndKeepSessionReadyAsync(string source)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        Guid sessionId = harness.Server.Sessions.ShouldHaveSingleItem().Id;

        // Act
        var error = await Should.ThrowAsync<GraphClientException>(async () =>
        { await foreach (var path in connection.QueryPathsAsync(source, cancellationToken: harness.Token)) { } });

        // Assert
        error.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        error.Message.ShouldContain("CG05001", Case.Sensitive);
        error.Message.ShouldContain("exactly one path or bound entity projection", Case.Sensitive);
        (await connection.QueryAsync("RETURN 1 AS one", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(1L);
        harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(sessionId);
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Client] - RETURN: duplicate column diagnostics complete rejection and preserve the connection")]
    [InlineData("RETURN 1 AS x, 2 AS x")]
    [InlineData("MATCH (a) RETURN a, a")]
    [InlineData("MATCH (a) RETURN a.x, a.x")]
    [InlineData("MATCH (a) RETURN a.x, 1 AS \"a.x\"")]
    [InlineData("RETURN 1, 2 AS \"1\"")]
    [InlineData("EXPLAIN RETURN 1 AS x, 2 AS x")]
    [InlineData("INSERT (:Unwritten) RETURN 1 AS x, 2 AS x")]
    public async Task QueryAsync_DuplicateColumnNames_ShouldRejectBeforeOutputAndKeepSessionReadyAsync(string source)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        Guid sessionId = harness.Server.Sessions.ShouldHaveSingleItem().Id;

        // Act
        var error = await Should.ThrowAsync<GraphClientException>(async () =>
            await connection.QueryAsync(source, cancellationToken: harness.Token));

        // Assert
        error.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        error.Message.ShouldContain("CG01015", Case.Sensitive);
        error.Message.ShouldContain("is declared more than once", Case.Sensitive);
        var result = await connection.QueryAsync("RETURN 1 AS x, 2 AS X", cancellationToken: harness.Token);
        result.Columns.Select(column => column.Name).ShouldBe(["x", "X"]);
        result.ShouldHaveSingleItem().ToArray().ShouldBe(new object?[] { 1L, 2L });
        (await connection.QueryAsync("SHOW LABELS", cancellationToken: harness.Token)).ShouldBeEmpty();
        harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(sessionId);
    }
}
