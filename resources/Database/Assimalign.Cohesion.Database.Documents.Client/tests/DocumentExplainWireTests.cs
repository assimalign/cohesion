using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Documents.Client.Tests;

public sealed class DocumentExplainWireTests
{
    [Theory(DisplayName = "Cohesion Test [Documents.Client] - EXPLAIN: each description row is one JSON document on InMemory and TCP")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_Explain_ShouldStreamNamedJsonDocumentsAsync(bool tcp)
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.CreateCollectionAsync("items", harness.Token);
        await connection.PutAsync("items", "a", "{\"score\":5}"u8.ToArray(), cancellationToken: harness.Token);
        await connection.ExecuteAsync("CREATE INDEX by_score ON items (score)", cancellationToken: harness.Token);
        const string source = "EXPLAIN SELECT score FROM items WHERE score >= 2 AND score < 10";
        await using var session = await harness.Database.CreateSessionAsync(harness.Token);
        await using var description = (await session.ExecuteAsync(source, cancellationToken: harness.Token)).ShouldBeOfType<DatabasePlanDescription>();
        var expected = new List<object?[]>();
        await foreach (var row in description.GetRowsAsync(harness.Token))
        { expected.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        // Act
        var result = await connection.QueryAsync(source, cancellationToken: harness.Token);
        // Assert
        result.IsDocumentQuery.ShouldBeTrue();
        result.Count.ShouldBe(expected.Count);
        for (int i = 0; i < result.Count; i++)
        {
            var document = result[i][0].ShouldBeOfType<JsonElement>();
            document.EnumerateObject().Select(member => member.Name).ShouldBe(description.Columns.Select(column => column.Name));
            foreach (var column in description.Columns)
            {
                var value = document.GetProperty(column.Name);
                object? actual = value.ValueKind switch
                {
                    JsonValueKind.Null => null,
                    JsonValueKind.Number => value.GetInt32(),
                    _ => value.GetString(),
                };
                actual.ShouldBe(expected[i][column.Ordinal]);
            }
        }
        (await connection.GetAsync("items", "a", harness.Token)).ShouldNotBeNull();
    }
}
