using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Client.Tests;

public sealed class DocumentSubqueryWireTests
{
    [Theory(DisplayName = "Cohesion Test [Documents.Client] - Subqueries: overdeep projections return a located error and leave both transports reusable")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_OverdeepSubquery_ShouldRefuseWithoutClosingSessionAsync(bool tcp)
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.CreateCollectionAsync("items", harness.Token);
        await connection.QueryAsync("INSERT INTO items (KEY, VALUE) VALUES ('a', {n:1})", cancellationToken: harness.Token);
        using var json = JsonDocument.Parse(new string('[', 255) + "0" + new string(']', 255), new JsonDocumentOptions { MaxDepth = 256 });
        var parameters = new System.Collections.Generic.Dictionary<string, object?> { ["p"] = json.RootElement };
        // Act
        var error = await Should.ThrowAsync<DocumentClientException>(async () => await connection.QueryAsync(
            "SELECT (SELECT RAW @p FROM items) AS v FROM items", parameters, harness.Token));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05015");
        error.Diagnostics.Single().Start.ShouldNotBeNull();
        connection.IsOpen.ShouldBeTrue();
        (await connection.QueryAsync("SELECT n FROM items", cancellationToken: harness.Token)).Single()[0].ShouldBe(1m);
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Client] - Subqueries: arrays EXISTS IN and deferred forms cross both transports")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_Subqueries_ShouldPreserveValuesDiagnosticsAndSessionAsync(bool tcp)
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.CreateCollectionAsync("items", harness.Token);
        await connection.QueryAsync("INSERT INTO items (KEY, VALUE) VALUES ('a', {n:1}), ('b', {n:2})", cancellationToken: harness.Token);
        // Act
        var rows = await connection.QueryAsync("SELECT (SELECT RAW n FROM items) AS raw_n, (SELECT n FROM items WHERE n = 1) AS objects, EXISTS (SELECT RAW n FROM items WHERE n > 9) AS empty, n IN (SELECT RAW n FROM items WHERE n = 1) AS member FROM items", cancellationToken: harness.Token);
        // Assert
        rows.Count.ShouldBe(2);
        ((JsonElement)rows[0][0]!).GetRawText().ShouldBe("[1,2]");
        ((JsonElement)rows[0][1]!).GetRawText().ShouldBe("[{\"n\":1}]");
        rows.All(row => Equals(row[2], false)).ShouldBeTrue();
        rows.Select(row => row[3]).ShouldBe(new object?[] { true, false });
        var lazy = await connection.QueryAsync("SELECT CASE WHEN TRUE THEN 7 ELSE (SELECT RAW 1/0 FROM items) END AS n FROM items LIMIT 1", cancellationToken: harness.Token);
        lazy.Single()[0].ShouldBe(7m);
        var plan = await connection.QueryAsync("EXPLAIN SELECT (SELECT RAW n FROM items) FROM items", cancellationToken: harness.Token);
        plan.Documents.ShouldContain(row => row.GetProperty("operator").GetString() == "SubqueryExpression" && row.GetProperty("detail").GetString()!.Contains("cached_for_statement"));
        var error = await Should.ThrowAsync<DocumentClientException>(async () => await connection.QueryAsync("SELECT (SELECT RAW d.n FROM items s) FROM items d", cancellationToken: harness.Token));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CK00001");
        (await connection.QueryAsync("SELECT n FROM items WHERE n IN (SELECT RAW n FROM items)", cancellationToken: harness.Token)).Count.ShouldBe(2);
        connection.IsOpen.ShouldBeTrue();
    }
}
