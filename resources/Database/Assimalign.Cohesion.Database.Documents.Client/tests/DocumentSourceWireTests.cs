using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Client.Tests;

public sealed class DocumentSourceWireTests
{
    [Theory(DisplayName = "Cohesion Test [Documents.Client] - FROM: derived and expression rows and diagnostics cross both transports")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_FromSources_ShouldPreserveWireResultsAndSessionAsync(bool tcp)
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.CreateCollectionAsync("items", harness.Token);
        await connection.QueryAsync("INSERT INTO items (KEY, VALUE) VALUES ('a', {n:1}), ('b', {n:2})", cancellationToken: harness.Token);
        // Act
        var rows = await connection.QueryAsync("SELECT d.n FROM (SELECT n FROM items) d WHERE d.n > 0 ORDER BY d.n DESC LIMIT 1", cancellationToken: harness.Token);
        var values = await connection.QueryAsync("SELECT RAW v FROM [1,NULL,{n:2}] AS v", cancellationToken: harness.Token);
        var star = await connection.QueryAsync("SELECT * FROM (SELECT n FROM items LIMIT 1) d", cancellationToken: harness.Token);
        var metadata = await connection.QueryAsync("SELECT META(d).id AS id FROM [1] AS d", cancellationToken: harness.Token);
        using var parameter = JsonDocument.Parse("[1,2]");
        var parameterRows = await connection.QueryAsync("SELECT RAW v FROM @p AS v",
            new System.Collections.Generic.Dictionary<string, object?> { ["p"] = parameter.RootElement }, harness.Token);
        // Assert
        rows.Single()[0].ShouldBe(2m);
        values.Count.ShouldBe(3);
        values.Documents[0].GetInt32().ShouldBe(1);
        values.Documents[1].ValueKind.ShouldBe(JsonValueKind.Null);
        values.Documents[2].GetProperty("n").GetInt32().ShouldBe(2);
        star.Documents.Single().GetRawText().ShouldBe("{\"d\":{\"n\":1}}");
        metadata.Single()[0].ShouldBeNull();
        parameterRows.Documents.Select(document => document.GetInt32()).ShouldBe([1, 2]);
        (await connection.QueryAsync("SELECT RAW v FROM MISSING AS v", cancellationToken: harness.Token)).ShouldBeEmpty();
        var plan = await connection.QueryAsync("EXPLAIN SELECT d.n FROM (SELECT n FROM items) d", cancellationToken: harness.Token);
        plan.Documents.ShouldContain(row => row.GetProperty("operator").GetString() == "DerivedSource");
        var error = await Should.ThrowAsync<DocumentClientException>(async () => await connection.QueryAsync("SELECT * FROM (SELECT n FROM items)", cancellationToken: harness.Token));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD00015");
        connection.IsOpen.ShouldBeTrue();
        (await connection.QueryAsync("SELECT RAW v FROM (SELECT RAW n FROM items) v", cancellationToken: harness.Token)).Count.ShouldBe(2);
    }
}
