using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Types;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Client.Tests;

public sealed class GraphLinearCompositionWireTests
{
    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Composition: values, filters, FOR and WITH complete over both transports")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_LinearStatements_ShouldRoundTripRowsAndTypesAsync(bool tcp)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        var parameters = new Dictionary<string, object?> { ["xs"] = new object?[] { 1L, 2L }, ["value"] = 1L };
        // Act / Assert
        var scalar = await connection.QueryAsync("LET n = $value FILTER n > 0 RETURN n NEXT LET y = n + 1 RETURN y", parameters, harness.Token);
        scalar.ShouldHaveSingleItem().ToArray().ShouldBe(new object?[] { 2L });
        foreach (string source in new[] { "FOR x IN $xs RETURN x", "FOR x IN [1,2] RETURN x", "UNWIND [1,2] AS x WITH ALL x RETURN x" })
        { (await connection.QueryAsync(source, parameters, harness.Token)).Select(row => row[0]).ShouldBe(new object?[] { 1L, 2L }); }
        (await connection.ExecuteAsync("FOR x IN $xs INSERT (:P {k:x})", parameters, harness.Token)).ShouldBe(2);
        var count = await connection.QueryAsync("MATCH (a:P) WITH count(a) AS c RETURN c", cancellationToken: harness.Token);
        count.Columns.ShouldHaveSingleItem().Type.ShouldBe(DatabaseType.Int64);
        count.ShouldHaveSingleItem()[0].ShouldBe(2L);
        (await connection.QueryAsync("MATCH (a:P) WITH a ORDER BY a.k DESC LIMIT 1 WHERE a.k > 1 RETURN a.k", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(2L);
        (await connection.QueryAsync("INSERT (:Fresh {k:1}) NEXT MATCH (a:Fresh) RETURN a.k", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(1L);
        (await connection.QueryAsync("INSERT (:FreshTwo {k:2}) MATCH (a:FreshTwo) RETURN a.k", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(2L);
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Composition: carried entities retain ExecutePaths gating and optionality")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryPathsAsync_Composition_ShouldPreserveEntitiesAndRejectWritesAsync(bool tcp)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.ExecuteAsync("INSERT (a:P {name:'Alice'})-[:T]->(:P {name:'Bob'})", cancellationToken: harness.Token);
        // Act / Assert
        foreach (string source in new[] { "MATCH (a:P) WITH a RETURN a", "MATCH (a:P) RETURN a NEXT RETURN a", "MATCH p=(a)-[:T]->(b) WITH p AS q RETURN q" })
        {
            var names = new List<object?>();
            await foreach (var path in connection.QueryPathsAsync(source, cancellationToken: harness.Token)) { names.Add(path.Nodes[0].Properties["name"]); }
            names.ShouldNotBeEmpty();
            var error = await Should.ThrowAsync<GraphClientException>(async () => await connection.QueryAsync(source, cancellationToken: harness.Token));
            error.Message.ShouldContain("ExecutePaths", Case.Sensitive);
        }
        foreach (string source in new[] { "INSERT (a:P) WITH a RETURN a", "OPTIONAL MATCH (a:P) WITH a RETURN a", "OPTIONAL MATCH p=(a)-[:T]->(b) RETURN p NEXT RETURN p" })
        {
            var error = await Should.ThrowAsync<GraphClientException>(async () =>
            { await foreach (var path in connection.QueryPathsAsync(source, cancellationToken: harness.Token)) { } });
            error.Message.ShouldContain("COHDBG001", Case.Sensitive);
        }
        (await connection.QueryAsync("MATCH (a:P) RETURN count(a)", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(2L);
    }
}
