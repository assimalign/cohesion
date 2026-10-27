using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Graph.Client.Tests;

public sealed class GraphExplainWireTests
{
    [Fact(DisplayName = "Cohesion Test [Graph.Client] - EXPLAIN: Execute admits entity projections and returns the same chosen index rows")]
    public async Task QueryAsync_ExplainEntityMatch_ShouldReturnSharedRowsAsync()
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync();
        await using var session = await harness.Database.CreateSessionAsync(harness.Token);
        await session.ExecuteAsync("INSERT (:Person {name:'Ada'})", cancellationToken: harness.Token);
        await GraphSchema.Open(harness.Database, session).CreateIndexAsync("Person", "by_name", "name", harness.Token);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        foreach (string source in new[] { "EXPLAIN MATCH (a:Person {name:'Ada'}) RETURN a", "EXPLAIN MATCH (a:Person) RETURN a" })
        {
            await using var description = (await session.ExecuteAsync(source, cancellationToken: harness.Token)).ShouldBeOfType<DatabasePlanDescription>();
            var expected = new List<object?[]>();
            await foreach (var row in description.GetRowsAsync(harness.Token))
            { expected.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
            // Act
            var result = await connection.QueryAsync(source, cancellationToken: harness.Token);
            // Assert
            result.Columns.Select(column => column.Name).ShouldBe(description.Columns.Select(column => column.Name));
            result.Columns.Select(column => column.Type).ShouldBe(description.Columns.Select(column => column.Type));
            result.Count.ShouldBe(expected.Count);
            for (int i = 0; i < result.Count; i++)
            { Enumerable.Range(0, 8).Select(ordinal => result[i][ordinal]).ShouldBe(expected[i]); }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Client] - EXPLAIN: ExecutePaths refuses descriptions and retains the connection")]
    public async Task QueryPathsAsync_Explain_ShouldFailWithCodedErrorAndReuseConnectionAsync()
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        // Act
        var error = await Should.ThrowAsync<GraphClientException>(async () =>
        {
            await foreach (var path in connection.QueryPathsAsync("EXPLAIN MATCH (a:Person) RETURN a", cancellationToken: harness.Token)) { _ = path; }
        });
        // Assert
        error.Message.ShouldContain("CG05001", Case.Sensitive);
        connection.IsOpen.ShouldBeTrue();
        (await connection.QueryAsync("EXPLAIN MATCH (n) RETURN n", cancellationToken: harness.Token)).ShouldNotBeEmpty();
    }
}
