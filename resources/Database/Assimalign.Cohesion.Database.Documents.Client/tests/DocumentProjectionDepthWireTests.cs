using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Client.Tests;

public sealed class DocumentProjectionDepthWireTests
{
    [Theory(DisplayName = "Cohesion Test [Documents.Client] - Projection: wrapped stars fail without losing either session")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task QueryAsync_StarDepth_ShouldPreserveSessionAsync(bool tcp, bool nested)
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        var session = harness.Server.Sessions.ShouldHaveSingleItem().Id;
        await connection.CreateCollectionAsync("items", harness.Token);
        await connection.QueryAsync("INSERT INTO items (KEY, VALUE) VALUES ('a', {n:1})", cancellationToken: harness.Token);
        using var json = JsonDocument.Parse(new string('[', 255) + "0" + new string(']', 255), new JsonDocumentOptions { MaxDepth = 256 });
        var parameters = new Dictionary<string, object?> { ["deep"] = json.RootElement };
        string suffix = " FROM items d UNNEST " + (nested ? "(SELECT RAW @deep FROM items LIMIT 1)" : "[@deep]") + " AS v LIMIT 1";
        // Act / Assert
        var error = await Should.ThrowAsync<DocumentClientException>(async () => await connection.QueryAsync("SELECT *, 1 AS w" + suffix, parameters, harness.Token));
        var diagnostic = error.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("CD05015");
        diagnostic.Start.ShouldBe(7);
        diagnostic.End.ShouldBe(8);
        var rows = await connection.QueryAsync("SELECT *" + suffix, parameters, harness.Token);
        rows.Count.ShouldBe(1);
        rows.Documents.Single().GetProperty("v").ValueKind.ShouldBe(JsonValueKind.Array);
        (await connection.QueryAsync("SELECT n FROM items", cancellationToken: harness.Token)).Single()[0].ShouldBe(1m);
        connection.IsOpen.ShouldBeTrue();
        harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(session);
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Client] - Projection: developer JSON failures retain locations and both sessions")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task QueryAsync_DeveloperJson_ShouldPreserveSessionAsync(bool tcp, bool nested)
    {
        // Arrange
        using var json = JsonDocument.Parse(new string('[', 256) + "0" + new string(']', 256), new JsonDocumentOptions { MaxDepth = 256 });
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp, configureEngine: builder =>
            builder.Functions.Add(DocumentScalarFunction.Create<decimal, DocumentValue>("deep_value", _ => DocumentValue.FromJson(json.RootElement))));
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        var session = harness.Server.Sessions.ShouldHaveSingleItem().Id;
        await connection.CreateCollectionAsync("items", harness.Token);
        await connection.QueryAsync("INSERT INTO items (KEY, VALUE) VALUES ('a', {n:1})", cancellationToken: harness.Token);
        // Act
        var error = await Should.ThrowAsync<DocumentClientException>(async () => await connection.QueryAsync(
            "SELECT deep_value(1) AS v FROM items" + (nested ? " WHERE EXISTS (SELECT RAW 1 FROM items)" : ""), cancellationToken: harness.Token));
        // Assert
        var diagnostic = error.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("CD05015");
        diagnostic.Start.ShouldBe(7);
        diagnostic.End.ShouldBe(20);
        (await connection.QueryAsync("SELECT n FROM items", cancellationToken: harness.Token)).Single()[0].ShouldBe(1m);
        connection.IsOpen.ShouldBeTrue();
        harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(session);
    }
}
