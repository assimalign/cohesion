using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Types;

using Assimalign.Cohesion.Database.Documents.Protocol;

namespace Assimalign.Cohesion.Database.Documents.Client.Tests;

public sealed class DocumentClientPagingTests
{
    [Theory(DisplayName = "Cohesion Test [Documents.Client] - Paging: frames, count, raw bytes and column types survive both transports")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_Paging_ShouldPreserveRowsAndCompletionAsync(bool tcp)
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.CreateCollectionAsync("items", harness.Token);
        await connection.PutAsync("items", "c", " {\"v\":3} "u8.ToArray(), cancellationToken: harness.Token);
        await connection.PutAsync("items", "b", " {\"v\":\"x\"} "u8.ToArray(), cancellationToken: harness.Token);
        await connection.PutAsync("items", "a", " {\"v\":1} "u8.ToArray(), cancellationToken: harness.Token);
        // Act / Assert: QueryAsync also rejects a Complete result count unequal to the Document frames.
        var page = await connection.QueryAsync("SELECT * FROM items OFFSET $skip LIMIT @n",
            new Dictionary<string, object?> { ["n"] = 2, ["skip"] = 1 }, harness.Token);
        page.Count.ShouldBe(2);
        page.Documents.Count.ShouldBe(2);
        page.AffectedCount.ShouldBe(-1);
        page.IsDocumentQuery.ShouldBeTrue();
        page[0].GetBytes(0).ToArray().ShouldBe(" {\"v\":\"x\"} "u8.ToArray());
        page[1].GetBytes(0).ToArray().ShouldBe(" {\"v\":3} "u8.ToArray());
        var empty = await connection.QueryAsync("SELECT * FROM items LIMIT 0", cancellationToken: harness.Token);
        empty.ShouldBeEmpty();
        empty.Documents.ShouldBeEmpty();
        empty.IsDocumentQuery.ShouldBeTrue();
        empty.Columns.ShouldHaveSingleItem();
        empty.AffectedCount.ShouldBe(-1);
        foreach (string suffix in new[] { "", " LIMIT 1", " LIMIT 0", " OFFSET 10" })
        {
            var projected = await connection.QueryAsync("SELECT v FROM items ORDER BY v" + suffix, cancellationToken: harness.Token);
            projected.Columns.Single().Type.ShouldBe(DatabaseType.Null);
        }
        var count = await connection.QueryAsync("SELECT COUNT(*) FROM items LIMIT 0", cancellationToken: harness.Token);
        count.ShouldBeEmpty();
        count.Columns.Single().Type.ShouldBe(DatabaseType.Int64);
        (await connection.QueryAsync("SELECT v FROM items OFFSET 9223372036854775807 LIMIT 9223372036854775807", cancellationToken: harness.Token)).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Client] - Paging: parameter normalization failures preserve OQL0007 and session reuse")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_InvalidPagingParameters_ShouldPreserveDiagnosticsAsync(bool tcp)
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.CreateCollectionAsync("items", harness.Token);
        foreach (string json in new[] { "-1", "1.5", "\"2\"", "null", "true", "9223372036854775808", "1e999" })
        {
            foreach (string clause in new[] { "LIMIT", "OFFSET" })
            {
                using var value = JsonDocument.Parse(json);
                var parameters = new Dictionary<string, object?> { ["n"] = value.RootElement };
                string query = "SELECT * FROM items " + clause + " @n";
                // Act
                var failure = await Should.ThrowAsync<DocumentClientException>(async () =>
                    await connection.QueryAsync(query, parameters, harness.Token));
                // Assert
                failure.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
                var diagnostic = failure.Diagnostics.ShouldHaveSingleItem();
                diagnostic.Code.ShouldBe("OQL0007");
                diagnostic.Message.ShouldBe($"{clause} requires a non-negative integer in the signed 64-bit range.");
                diagnostic.Start.ShouldBe(query.Length - 2);
                diagnostic.End.ShouldBe(query.Length);
                connection.IsOpen.ShouldBeTrue();
            }
        }
        (await connection.QueryAsync("SELECT * FROM items LIMIT @n", new Dictionary<string, object?> { ["n"] = 2 }, harness.Token)).ShouldBeEmpty();
    }
}
