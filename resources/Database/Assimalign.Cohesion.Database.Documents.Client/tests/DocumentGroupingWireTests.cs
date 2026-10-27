using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents.Client.Tests;

public sealed class DocumentGroupingWireTests
{
    [Theory(DisplayName = "Cohesion Test [Documents.Client] - GROUP AS: wrappers aliases scopes and empty metadata cross both transports")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_GroupAs_ShouldRoundTripBindingsAsync(bool tcp)
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.CreateCollectionAsync("items", harness.Token);
        await connection.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('a',{k:'x',name:'alpha',tags:[1,2]}),('b',{k:'x',name:'beta',tags:[]}),('c',{k:NULL,name:'gamma',tags:[NULL]}),('d',{name:'delta'})", cancellationToken: harness.Token);

        // Act
        var result = await connection.QueryAsync("SELECT key, g, COUNT(key) AS n FROM items d LEFT UNNEST d.tags t GROUP BY d.k AS key GROUP AS g ORDER BY key", cancellationToken: harness.Token);

        // Assert
        result.Count.ShouldBe(3);
        result.Columns.Select(x => x.Type).ShouldBe([DatabaseType.String, DatabaseType.Json, DatabaseType.Int64]);
        result[0].IsMissing(0).ShouldBeTrue();
        result[0][1].ShouldBeOfType<JsonElement>()[0].TryGetProperty("key", out _).ShouldBeFalse();
        result[1].IsNull(0).ShouldBeTrue();
        result[1][1].ShouldBeOfType<JsonElement>()[0].GetProperty("t").ValueKind.ShouldBe(JsonValueKind.Null);
        result[2][0].ShouldBe("x");
        result[2][2].ShouldBe(3L);
        var wrappers = result[2][1].ShouldBeOfType<JsonElement>();
        wrappers.GetArrayLength().ShouldBe(3);
        wrappers.EnumerateArray().Count(x => x.TryGetProperty("t", out _)).ShouldBe(2);
        foreach (var wrapper in wrappers.EnumerateArray())
        { wrapper.GetProperty("key").GetString().ShouldBe("x"); wrapper.GetProperty("d").GetProperty("k").GetString().ShouldBe("x"); }
        var mapped = await connection.QueryAsync("SELECT ARRAY r.d.name FOR r IN g END AS names, FIRST r.d.name FOR r IN g WHEN r.d.name = 'beta' END AS first_name, EVERY key IN [TRUE] SATISFIES key END AS shadowed FROM items d GROUP BY d.k key GROUP AS g HAVING ANY r IN g SATISFIES r.d.name = 'alpha' END ORDER BY ARRAY_LENGTH(g) DESC", cancellationToken: harness.Token);
        mapped.ShouldHaveSingleItem()[0].ShouldBeOfType<JsonElement>().EnumerateArray().Select(x => x.GetString()).Order().ShouldBe(["alpha", "beta"]);
        mapped[0][1].ShouldBe("beta"); mapped[0][2].ShouldBe(true);
        var empty = await connection.QueryAsync("SELECT VALUE g FROM items WHERE FALSE GROUP BY TRUE GROUP AS g", cancellationToken: harness.Token);
        empty.ShouldBeEmpty(); empty.Columns.ShouldHaveSingleItem().Type.ShouldBe(DatabaseType.Json);
        var explain = await connection.QueryAsync("EXPLAIN SELECT g FROM items GROUP BY 1/0 AS key GROUP AS g", cancellationToken: harness.Token);
        explain.Documents.ShouldContain(x => x.GetProperty("detail").ValueKind == JsonValueKind.String && x.GetProperty("detail").GetString() == "group_as=g; group_aliases=key");
        await connection.ExecuteAsync("DELETE FROM items", cancellationToken: harness.Token);
        wrappers[0].GetProperty("d").GetProperty("name").GetString().ShouldNotBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Client] - GROUP AS: syntax and binding failures preserve diagnostics and the wire session")]
    [InlineData(false, "SELECT g FROM items GROUP AS g", "CD00012")]
    [InlineData(true, "SELECT g FROM items GROUP AS g", "CD00012")]
    [InlineData(false, "SELECT g FROM items GROUP BY k AS g GROUP AS g", "CD01030")]
    [InlineData(true, "SELECT g FROM items GROUP BY k AS g GROUP AS g", "CD01030")]
    [InlineData(false, "SELECT ARRAY_AGG(g) FROM items GROUP BY k GROUP AS g", "CD01031")]
    [InlineData(true, "SELECT ARRAY_AGG(g) FROM items GROUP BY k GROUP AS g", "CD01031")]
    public async Task QueryAsync_InvalidGrouping_ShouldPreserveSessionAsync(bool tcp, string query, string code)
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.CreateCollectionAsync("items", harness.Token);
        // Act
        var failure = await Should.ThrowAsync<DocumentClientException>(async () => await connection.QueryAsync(query, cancellationToken: harness.Token));
        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
        failure.Diagnostics[0].Start.ShouldNotBeNull();
        (await connection.QueryAsync("SELECT COUNT(*) FROM items", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(0L);
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Client] - GROUP AS: wrapper exhaustion preserves CD05018 and connection reuse")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_GroupWrapperBudget_ShouldRefuseAsync(bool tcp)
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp, configureEngine: builder => builder.Options.StatementMemoryBudget = 8192);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.CreateCollectionAsync("items", harness.Token);
        await connection.PutAsync("items", "a", Encoding.UTF8.GetBytes("{\"pad\":\"" + new string('p', 1200) + "\"}"), cancellationToken: harness.Token);
        // Act
        var failure = await Should.ThrowAsync<DocumentClientException>(async () => await connection.QueryAsync("SELECT ARRAY_LENGTH(g) FROM items UNNEST [1,2,3,4] x GROUP BY TRUE GROUP AS g", cancellationToken: harness.Token));
        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05018");
        failure.Diagnostics[0].Start.ShouldNotBeNull();
        (await connection.QueryAsync("SELECT COUNT(*) FROM items UNNEST [1,2,3,4] x GROUP BY TRUE", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(4L);
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Client] - DML: pending-buffer budget failure leaves every original image intact (#1755)")]
    [InlineData(false, "UPDATE items SET pad = @v")]
    [InlineData(true, "UPDATE items SET pad = @v")]
    [InlineData(false, "DELETE FROM items")]
    [InlineData(true, "DELETE FROM items")]
    [InlineData(false, "UPSERT INTO items (KEY, VALUE) VALUES ('a',@v),('b',@v)")]
    [InlineData(true, "UPSERT INTO items (KEY, VALUE) VALUES ('a',@v),('b',@v)")]
    public async Task ExecuteAsync_PendingBudget_ShouldRollbackAndReuseSessionAsync(bool tcp, string query)
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp, configureEngine: builder => builder.Options.StatementMemoryBudget = 1024);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.CreateCollectionAsync("items", harness.Token);
        string first = new('a', 400), second = new('b', 400);
        await connection.PutAsync("items", new(first), "{}"u8.ToArray(), cancellationToken: harness.Token);
        await connection.PutAsync("items", new(second), "{}"u8.ToArray(), cancellationToken: harness.Token);
        // Act
        var failure = await Should.ThrowAsync<DocumentClientException>(async () => await connection.ExecuteAsync(query,
            new Dictionary<string, object?> { ["v"] = new string('x', 600) }, harness.Token));
        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05018");
        (await connection.GetAsync("items", new(first), harness.Token)).ShouldNotBeNull().Content.Length.ShouldBe(2);
        (await connection.GetAsync("items", new(second), harness.Token)).ShouldNotBeNull().Content.Length.ShouldBe(2);
        (await connection.GetAsync("items", "a", harness.Token)).ShouldBeNull();
    }
}
