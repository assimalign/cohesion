using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Documents.Client.Tests;

public sealed class DocumentClientWireTests
{
    [Theory(DisplayName = "Cohesion Test [Documents.Client] - Eighty-level documents survive whole and projected results")]
    [InlineData("SELECT * FROM items", true)]
    [InlineData("SELECT nested FROM items", false)]
    public async Task QueryAsync_DeepDocument_ShouldHonorConfiguredJsonDepth(string statement, bool whole)
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.CreateCollectionAsync("items", harness.Token);
        string nested = new string('[', 80) + "0" + new string(']', 80);
        byte[] content = Encoding.UTF8.GetBytes("{\"nested\":" + nested + "}");
        await connection.PutAsync("items", "deep", content, cancellationToken: harness.Token);

        // Act
        var result = await connection.QueryAsync(statement, cancellationToken: harness.Token);

        // Assert
        (await connection.GetAsync("items", "deep", harness.Token)).ShouldNotBeNull().Content.ToArray().ShouldBe(content);
        var value = result.ShouldHaveSingleItem()[0].ShouldBeOfType<JsonElement>();
        (whole ? value.GetProperty("nested") : value).GetRawText().ShouldBe(nested);
        connection.IsOpen.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Client] - Eighty-level parameters cross the wire and return as JSON")]
    public async Task QueryAsync_DeepParameter_ShouldHonorConfiguredJsonDepth()
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.CreateCollectionAsync("items", harness.Token);
        await connection.PutAsync("items", "one", "{}"u8.ToArray(), cancellationToken: harness.Token);
        string nested = new string('[', 80) + "0" + new string(']', 80);
        using var json = JsonDocument.Parse(nested, new JsonDocumentOptions { MaxDepth = 256 });

        // Act
        var result = await connection.QueryAsync("SELECT @value AS nested FROM items",
            new Dictionary<string, object?> { ["value"] = json.RootElement }, harness.Token);

        // Assert
        result.ShouldHaveSingleItem()[0].ShouldBeOfType<JsonElement>().GetRawText().ShouldBe(nested);
        connection.IsOpen.ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Client] - CRUD preserves versions, bytes and collections on both transports")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Commands_BothTransports_ShouldPreserveMutationSemantics(bool tcp)
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        byte[] content = " {\"city\":\"東京\", \"nested\":[1,null,{\"active\":true}]} "u8.ToArray();

        // Act / Assert
        (await connection.GetCollectionsAsync(harness.Token)).ShouldBeEmpty();
        (await connection.CreateCollectionAsync("items", harness.Token)).ShouldBe("items");
        (await connection.GetCollectionAsync("items", harness.Token)).ShouldBe("items");
        (await connection.GetCollectionsAsync(harness.Token)).ShouldBe(["items"]);
        (await connection.GetAsync("items", "one", harness.Token)).ShouldBeNull();
        var saved = await connection.PutAsync("items", "one", content, cancellationToken: harness.Token);
        saved.Id.Value.ShouldBe("one");
        saved.Content.ToArray().ShouldBe(content);
        saved.Version.Value.ShouldBeGreaterThan(0UL);
        var read = (await connection.GetAsync("items", "one", harness.Token)).ShouldNotBeNull();
        read.Version.ShouldBe(saved.Version);
        read.Content.ToArray().ShouldBe(content);
        var replaced = await connection.PutAsync("items", "one", "[1,true,null]"u8.ToArray(), saved.Version, harness.Token);
        replaced.Version.Value.ShouldBeGreaterThan(saved.Version.Value);
        var stale = await Should.ThrowAsync<DocumentClientException>(async () =>
            await connection.PutAsync("items", "one", "{}"u8.ToArray(), saved.Version, harness.Token));
        stale.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        connection.IsOpen.ShouldBeTrue();
        (await connection.GetAsync("items", "one", harness.Token)).ShouldNotBeNull().Content.ToArray().ShouldBe("[1,true,null]"u8.ToArray());
        await Should.ThrowAsync<DocumentClientException>(async () => await connection.DeleteAsync("items", "one", saved.Version, harness.Token));
        (await connection.DeleteAsync("items", "one", replaced.Version, harness.Token)).ShouldBeTrue();
        (await connection.DeleteAsync("items", "one", cancellationToken: harness.Token)).ShouldBeFalse();
        (await connection.GetAsync("items", "one", harness.Token)).ShouldBeNull();
        var reinserted = await connection.PutAsync("items", "one", "null"u8.ToArray(), cancellationToken: harness.Token);
        reinserted.Version.Value.ShouldBeGreaterThan(replaced.Version.Value);
        await connection.CreateCollectionAsync("Other", harness.Token);
        (await connection.GetCollectionsAsync(harness.Token)).ShouldBe(["Other", "items"]);
        await connection.DropCollectionAsync("items", harness.Token);
        (await connection.GetCollectionsAsync(harness.Token)).ShouldBe(["Other"]);
        await Should.ThrowAsync<DocumentClientException>(async () => await connection.GetCollectionAsync("items", harness.Token));
        await connection.DropCollectionAsync("Other", harness.Token);
        (await connection.GetCollectionsAsync(harness.Token)).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Client] - Every advertised OQL clause crosses both transports")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_AllSupportedClauses_ShouldPreserveEngineResults(bool tcp)
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        (await connection.ExecuteAsync("CREATE COLLECTION items", cancellationToken: harness.Token)).ShouldBe(0);
        await connection.PutAsync("items", "a", "{\"name\":\"Ada\",\"group\":\"A\",\"score\":2,\"active\":true,\"nested\":{\"x\":5},\"array\":[9,null]}"u8.ToArray(), cancellationToken: harness.Token);
        await connection.PutAsync("items", "b", "{\"name\":\"Bob\",\"group\":\"A\",\"score\":4,\"active\":false}"u8.ToArray(), cancellationToken: harness.Token);
        await connection.PutAsync("items", "c", "{\"name\":\"Cy\",\"group\":\"B\",\"score\":1,\"active\":true}"u8.ToArray(), cancellationToken: harness.Token);

        // Act / Assert: SELECT, FROM alias, WHERE, paths, parameters, ORDER BY and arithmetic.
        var rows = await connection.QueryAsync("SELECT d.name AS person, d.nested.x AS x, d.array[0] AS entry_value, d.score * 2 + 1 AS value FROM items AS d WHERE d.score >= @min AND NOT d.active = false ORDER BY value DESC",
            new Dictionary<string, object?> { ["min"] = 2 }, harness.Token);
        rows.Columns.Select(column => column.Name).ShouldBe(["person", "x", "entry_value", "value"]);
        rows.Columns.Select(column => column.Ordinal).ShouldBe([0, 1, 2, 3]);
        rows.ShouldHaveSingleItem().ToArray().ShouldBe(new object?[] { "Ada", 5m, 9m, 5m });
        rows[0]["person"].ShouldBe("Ada");
        rows.AffectedCount.ShouldBe(-1);
        rows.IsDocumentQuery.ShouldBeFalse();
        rows.Documents.ShouldBeEmpty();
        var grouped = await connection.QueryAsync("SELECT d.group AS category, COUNT(*) AS n, SUM(d.score) AS total, AVG(d.score) AS average, MIN(d.score) AS low, MAX(d.score) AS high FROM items d GROUP BY d.group HAVING SUM(d.score) > 2 ORDER BY total DESC", cancellationToken: harness.Token);
        grouped.ShouldHaveSingleItem().ToArray().ShouldBe(new object?[] { "A", 2m, 6m, 3m, 2m, 4m });
        (await connection.QueryAsync("SELECT name FROM items WHERE nested.x IS NULL OR score < 0 ORDER BY name ASC", cancellationToken: harness.Token))
            .Select(row => row[0]).ShouldBe(new object?[] { "Bob", "Cy" });
        (await connection.QueryAsync("SELECT name FROM items WHERE score <> 2 AND score != 4 AND score <= 1 AND score > 0", cancellationToken: harness.Token))
            .ShouldHaveSingleItem()[0].ShouldBe("Cy");
        (await connection.QueryAsync("SELECT -score + 10 / 2 % 3 AS value FROM items WHERE nested.x IS NOT NULL", cancellationToken: harness.Token))
            .ShouldHaveSingleItem()[0].ShouldBe(0m);

        // Whole documents and nested projected values retain JSON shape, including empty results.
        var whole = await connection.QueryAsync("SELECT * FROM items WHERE name = 'Ada'", cancellationToken: harness.Token);
        whole.IsDocumentQuery.ShouldBeTrue();
        whole.Documents.ShouldHaveSingleItem().GetProperty("nested").GetProperty("x").GetInt32().ShouldBe(5);
        whole[0][0].ShouldBeOfType<JsonElement>().GetProperty("array")[1].ValueKind.ShouldBe(JsonValueKind.Null);
        var nested = await connection.QueryAsync("SELECT d.nested, d.array FROM items d WHERE name = 'Ada'", cancellationToken: harness.Token);
        nested[0][0].ShouldBeOfType<JsonElement>().GetProperty("x").GetDecimal().ShouldBe(5m);
        var empty = await connection.QueryAsync("SELECT * FROM items WHERE score < 0", cancellationToken: harness.Token);
        empty.IsDocumentQuery.ShouldBeTrue();
        empty.ShouldBeEmpty();
        empty.Columns.Count.ShouldBe(1);
        using var parameter = JsonDocument.Parse("{\"x\":5}");
        (await connection.QueryAsync("SELECT name FROM items WHERE nested = @object", new Dictionary<string, object?> { ["object"] = parameter.RootElement }, harness.Token))
            .ShouldHaveSingleItem()[0].ShouldBe("Ada");

        // CREATE INDEX, DROP INDEX and both virtual catalog collections use the same wire path.
        (await connection.ExecuteAsync("CREATE INDEX by_score ON items (score)", cancellationToken: harness.Token)).ShouldBe(0);
        (await connection.QueryAsync("SELECT name FROM items WHERE score >= 2 ORDER BY score DESC", cancellationToken: harness.Token))
            .Select(row => row[0]).ShouldBe(new object?[] { "Bob", "Ada" });
        var indexes = await connection.QueryAsync("SELECT COLLECTION_CATALOG, COLLECTION_NAME, INDEX_NAME, PATH, IS_UNIQUE FROM COHESION_SCHEMA.INDEXES", cancellationToken: harness.Token);
        indexes.ShouldHaveSingleItem().ToArray().ShouldBe(new object?[] { "documents", "items", "by_score", "score", false });
        (await connection.QueryAsync("SELECT OBJECT_NAME, OWNER FROM COHESION_SCHEMA.OBJECT_OWNERSHIP", cancellationToken: harness.Token))
            .ShouldHaveSingleItem().ToArray().ShouldBe(new object?[] { "items", "Adhoc" });
        (await connection.ExecuteAsync("DROP INDEX by_score ON items", cancellationToken: harness.Token)).ShouldBe(0);
        (await connection.QueryAsync("SELECT * FROM COHESION_SCHEMA.INDEXES", cancellationToken: harness.Token)).ShouldBeEmpty();
        // Collection DDL shares the OQL wire path; CRUD uses the newly created collection.
        (await connection.ExecuteAsync("CREATE COLLECTION IF NOT EXISTS items", cancellationToken: harness.Token)).ShouldBe(0);
        Encoding.UTF8.GetString((await connection.GetAsync("items", "a", harness.Token)).ShouldNotBeNull().Content.Span)
            .ShouldContain("\"name\":\"Ada\"", Case.Sensitive);
        (await connection.ExecuteAsync("DROP COLLECTION items", cancellationToken: harness.Token)).ShouldBe(0);
        (await connection.ExecuteAsync("DROP COLLECTION IF EXISTS items", cancellationToken: harness.Token)).ShouldBe(0);
        (await connection.QueryAsync("SELECT OBJECT_NAME FROM COHESION_SCHEMA.OBJECT_OWNERSHIP", cancellationToken: harness.Token)).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Client] - Scalar document roots remain whole JSON values")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_ScalarRoots_ShouldPreserveJsonKinds(bool tcp)
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.CreateCollectionAsync("roots", harness.Token);
        string[] json = ["null", "true", "42.5", "\"東京\"", "[1,null]", "{}"];
        for (int index = 0; index < json.Length; index++)
        {
            await connection.PutAsync("roots", index.ToString(), Encoding.UTF8.GetBytes(json[index]), cancellationToken: harness.Token);
        }

        // Act
        var invalid = await Should.ThrowAsync<DocumentClientException>(async () =>
            await connection.PutAsync("roots", "invalid", "1e100"u8.ToArray(), cancellationToken: harness.Token));
        var result = await connection.QueryAsync("SELECT * FROM roots", cancellationToken: harness.Token);

        // Assert
        invalid.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        result.Documents.Select(value => value.ValueKind).ShouldBe([JsonValueKind.Null, JsonValueKind.True, JsonValueKind.Number, JsonValueKind.String, JsonValueKind.Array, JsonValueKind.Object]);
        result.Documents[3].GetString().ShouldBe("東京");
        result[2][0].ShouldBeOfType<JsonElement>().GetRawText().ShouldBe("42.5");
        result.AffectedCount.ShouldBe(-1);
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Client] - Handshake database binding scopes all operations")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectAsync_SecondDatabase_ShouldKeepCatalogAndDataSeparate(bool tcp)
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp);
        await harness.Engine.CreateDatabaseAsync("other", harness.Token);
        await using var first = await harness.Client.ConnectAsync(harness.Token);
        await first.CreateCollectionAsync("visible", harness.Token);
        await using var client = DocumentClient.Create(new()
        {
            Settings = new DatabaseConnectionSettings { Database = "other", EndPoint = harness.Listener.EndPoint },
            ConnectionFactory = harness.Factory,
        });

        // Act
        await using var second = await client.ConnectAsync(harness.Token);

        // Assert
        second.Database.ShouldBe("other");
        (await second.GetCollectionsAsync(harness.Token)).ShouldBeEmpty();
        (await second.QueryAsync("SELECT * FROM COHESION_SCHEMA.OBJECT_OWNERSHIP", cancellationToken: harness.Token)).ShouldBeEmpty();
        await Should.ThrowAsync<DocumentClientException>(async () => await second.GetCollectionAsync("visible", harness.Token));
        (await first.GetCollectionAsync("visible", harness.Token)).ShouldBe("visible");
    }
}
