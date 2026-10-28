using System;
using System.Text.Json;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Client.Tests;

public sealed class DocumentComprehensionWireTests
{
    [Theory(DisplayName = "Cohesion Test [Documents.Client] - Comprehensions: SQL++ values scope and RETURNING round trip")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_Comprehensions_ShouldRoundTripOnBothTransports(bool tcp)
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.CreateCollectionAsync("items", harness.Token);
        await connection.PutAsync("items", "a", "{\"tags\":[{}, {\"a\":null}, {\"a\":2}]}"u8.ToArray(), cancellationToken: harness.Token);
        // Act
        var result = await connection.QueryAsync("SELECT ARRAY x.a FOR x IN tags END AS a, FIRST x.a FOR x IN tags END AS f, " +
            "ARRAY x FOR x IN MISSING END AS m, FIRST x FOR x IN NULL END AS n, " +
            "ARRAY [x, FIRST x FOR x IN [2] END, x] FOR x IN [1] END AS nested FROM items", cancellationToken: harness.Token);
        // Assert
        var row = result.ShouldHaveSingleItem();
        row[0].ShouldBeOfType<JsonElement>().GetRawText().ShouldBe("[null,2]");
        row.IsMissing(1).ShouldBeTrue();
        row.IsMissing(2).ShouldBeTrue();
        row.IsNull(3).ShouldBeTrue();
        row[4].ShouldBeOfType<JsonElement>().GetRawText().ShouldBe("[[1,2,1]]");
        var returning = await connection.QueryAsync("UPDATE items SET mapped = ARRAY x.a FOR x IN tags END " +
            "RETURNING ARRAY x FOR x IN mapped END AS a, FIRST x FOR x IN mapped END AS f", cancellationToken: harness.Token);
        returning.AffectedCount.ShouldBe(1);
        returning.ShouldHaveSingleItem()[0].ShouldBeOfType<JsonElement>().GetRawText().ShouldBe("[null,2]");
        returning[0].IsNull(1).ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Client] - Comprehensions: failures roll back writes and retain coded diagnostics")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_ComprehensionFailure_ShouldRollbackAndReuseSession(bool tcp)
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.CreateCollectionAsync("items", harness.Token);
        await connection.PutAsync("items", "a", "{\"n\":1}"u8.ToArray(), cancellationToken: harness.Token);
        // Act
        var failure = await Should.ThrowAsync<DocumentClientException>(async () => await connection.QueryAsync(
            "UPDATE items SET n = 2 RETURNING ARRAY 1 / x FOR x IN [1,0] END AS a", cancellationToken: harness.Token));
        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD02006");
        (await connection.QueryAsync("SELECT n FROM items", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(1m);
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Client] - Comprehensions: retained arrays consume the RETURNING statement budget")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_ComprehensionBudget_ShouldRollbackRetainedArrays(bool tcp)
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp,
            configureEngine: builder => builder.Options.StatementMemoryBudget = 1536);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.CreateCollectionAsync("items", harness.Token);
        await connection.PutAsync("items", "a", "{\"n\":1}"u8.ToArray(), cancellationToken: harness.Token);
        string text = new('x', 2000);
        // Act
        var failure = await Should.ThrowAsync<DocumentClientException>(async () => await connection.QueryAsync(
            $"UPDATE items SET n = 2 RETURNING ARRAY x FOR x IN ['{text}'] END AS a", cancellationToken: harness.Token));
        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05018");
        (await connection.QueryAsync("SELECT n FROM items", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(1m);
    }
}
