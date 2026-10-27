using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents.Client.Tests;

public sealed class DocumentAggregateWireTests
{
    [Theory(DisplayName = "Cohesion Test [Documents.Client] - Aggregates: quantifiers arrays booleans and statistics round trip")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_NewAggregates_ShouldRoundTripValuesAndMetadata(bool tcp)
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.CreateCollectionAsync("items", harness.Token);
        await connection.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('a',{v:1,b:TRUE,tags:[TRUE]}),('b',{v:1.0,b:FALSE,tags:[]}),('c',{v:3,b:1}),('d',{v:NULL,b:NULL}),('e',{})", cancellationToken: harness.Token);
        // Act
        var result = await connection.QueryAsync("SELECT COUNT(DISTINCT v) AS c, SUM(DISTINCT v) AS s, AVG(DISTINCT v) AS a, MIN(ALL v) AS lo, MAX(DISTINCT v) AS hi, ARRAY_AGG(DISTINCT v) AS ar, BOOL_AND(b) AS ba, BOOL_OR(b) AS bo, EVERY(b) AS e, STDDEV(DISTINCT v) AS sd, VARIANCE(DISTINCT v) AS va, EVERY(EVERY x IN tags SATISFIES x END) AS q FROM items HAVING COUNT(ALL v) > 0", cancellationToken: harness.Token);
        // Assert
        var row = result.ShouldHaveSingleItem();
        row[0].ShouldBe(2L); row[1].ShouldBe(4m); row[2].ShouldBe(2m); row[3].ShouldBe(1m); row[4].ShouldBe(3m);
        var array = row[5].ShouldBeOfType<JsonElement>();
        array.GetArrayLength().ShouldBe(3);
        array.EnumerateArray().Count(x => x.ValueKind == JsonValueKind.Null).ShouldBe(1);
        array.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetDecimal()).Order().ShouldBe([1m,3m]);
        row[6].ShouldBe(false); row[7].ShouldBe(true); row[8].ShouldBe(false);
        row[9].ShouldBeOfType<decimal>().ShouldBe(1.4142135623730951m);
        row[10].ShouldBe(2m); row[11].ShouldBe(true);
        result.Columns.Select(x => x.Type).ShouldBe([DatabaseType.Int64, DatabaseType.Decimal, DatabaseType.Decimal, DatabaseType.Decimal, DatabaseType.Decimal, DatabaseType.Json, DatabaseType.Boolean, DatabaseType.Boolean, DatabaseType.Boolean, DatabaseType.Decimal, DatabaseType.Decimal, DatabaseType.Boolean]);
        result.AffectedCount.ShouldBe(-1);
        var grouped = await connection.QueryAsync("SELECT v IS VALUED AS k, ARRAY_AGG(DISTINCT v) AS a, COUNT(DISTINCT v) AS n FROM items GROUP BY v IS VALUED HAVING EVERY(v IS VALUED) ORDER BY COUNT(DISTINCT v) DESC", cancellationToken: harness.Token);
        grouped.ShouldHaveSingleItem()[2].ShouldBe(2L);
        var empty = await connection.QueryAsync("SELECT ARRAY_AGG(v) AS a, BOOL_AND(b) AS b, BOOL_OR(b) AS o, EVERY(b) AS e, STDDEV(v) AS s, VARIANCE(v) AS v FROM items WHERE FALSE", cancellationToken: harness.Token);
        Enumerable.Range(0,6).All(empty.ShouldHaveSingleItem().IsNull).ShouldBeTrue();
        empty.Columns.Select(x => x.Type).ShouldBe([DatabaseType.Json,DatabaseType.Boolean,DatabaseType.Boolean,DatabaseType.Boolean,DatabaseType.Decimal,DatabaseType.Decimal]);
        var explain = await connection.QueryAsync("EXPLAIN SELECT ARRAY_AGG(DISTINCT v) AS a, EVERY(b) AS e FROM items", cancellationToken: harness.Token);
        explain.Documents.ShouldContain(x => x.GetProperty("detail").ValueKind == JsonValueKind.String && x.GetProperty("detail").GetString() == "aggregates=ARRAY_AGG(DISTINCT), EVERY(ALL)");
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Client] - Aggregates: syntax context and overflow diagnostics preserve the session")]
    [InlineData(false, "COUNT(DISTINCT *)", "CD00011")]
    [InlineData(true, "COUNT(DISTINCT *)", "CD00011")]
    [InlineData(false, "STDDEV(SUM(v))", "CD05001")]
    [InlineData(true, "STDDEV(SUM(v))", "CD05001")]
    [InlineData(false, "VARIANCE(v * 100000000000000000000)", "CD02007")]
    [InlineData(true, "VARIANCE(v * 100000000000000000000)", "CD02007")]
    public async Task QueryAsync_AggregateFailure_ShouldPreserveCodeAndReuseSession(bool tcp, string expression, string code)
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.CreateCollectionAsync("items", harness.Token);
        await connection.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('a',{v:1}),('b',{v:2})", cancellationToken: harness.Token);
        // Act
        var failure = await Should.ThrowAsync<DocumentClientException>(async () => await connection.QueryAsync("SELECT " + expression + " FROM items", cancellationToken: harness.Token));
        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
        failure.Diagnostics[0].Start.ShouldNotBeNull();
        (await connection.QueryAsync("SELECT COUNT(*) FROM items", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(2L);
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Client] - Aggregates: retained values exhaust the statement budget before any result")]
    [InlineData(false, "COUNT(DISTINCT v)")]
    [InlineData(true, "COUNT(DISTINCT v)")]
    [InlineData(false, "ARRAY_AGG(v)")]
    [InlineData(true, "ARRAY_AGG(v)")]
    [InlineData(false, "ARRAY_AGG(DISTINCT v)")]
    [InlineData(true, "ARRAY_AGG(DISTINCT v)")]
    public async Task QueryAsync_AggregateBudget_ShouldRetainCodeAndRollback(bool tcp, string aggregate)
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp,
            configureEngine: builder => builder.Options.StatementMemoryBudget = 6144);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.CreateCollectionAsync("items", harness.Token);
        await connection.PutAsync("items", "a", System.Text.Encoding.UTF8.GetBytes("{\"v\":\"" + new string('x',2000) + "\"}"), cancellationToken: harness.Token);
        await connection.ExecuteAsync("BEGIN", cancellationToken: harness.Token);
        await connection.PutAsync("items", "pending", "{}"u8.ToArray(), cancellationToken: harness.Token);
        // Act
        var failure = await Should.ThrowAsync<DocumentClientException>(async () => await connection.QueryAsync($"SELECT {aggregate} FROM items", cancellationToken: harness.Token));
        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05018");
        failure.Diagnostics[0].Start.ShouldBe(7);
        await connection.ExecuteAsync("ROLLBACK", cancellationToken: harness.Token);
        (await connection.QueryAsync("SELECT COUNT(*) FROM items", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(1L);
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Client] - Aggregates: extreme-range sample statistics retain decimal results on both transports")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_StatisticalRange_ShouldAvoidIntermediateRangeFailures(bool tcp)
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.CreateCollectionAsync("items", harness.Token);
        await connection.PutAsync("items", "a", "{}"u8.ToArray(), cancellationToken: harness.Token);
        // Act
        var tiny = await connection.QueryAsync("SELECT STDDEV(v) FROM items UNNEST [1e-20,2e-20] v", cancellationToken: harness.Token);
        var wide = await connection.QueryAsync("SELECT VARIANCE(v) AS v, STDDEV(v) AS s FROM items UNNEST [-2e14,2e14,0] v", cancellationToken: harness.Token);
        var root = await connection.QueryAsync("SELECT STDDEV(v) FROM items UNNEST [-1e20,1e20] v", cancellationToken: harness.Token);
        // Assert
        tiny.ShouldHaveSingleItem()[0].ShouldBe(7.0710678e-21m);
        wide.ShouldHaveSingleItem()[0].ShouldBe(4e28m);
        wide[0][1].ShouldBe(2e14m);
        root.ShouldHaveSingleItem()[0].ShouldBeOfType<decimal>().ShouldBe(1.4142135623730951e20m, 100000m);
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Client] - Aggregates: source and RETURNING combined retention rolls back on both transports")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_AggregateSourceReturningBudget_ShouldRollbackCombinedRetention(bool tcp)
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp,
            configureEngine: builder => builder.Options.StatementMemoryBudget = 1000);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.CreateCollectionAsync("items", harness.Token);
        await connection.CreateCollectionAsync("sink", harness.Token);
        await connection.PutAsync("items", "a", "{}"u8.ToArray(), cancellationToken: harness.Token);
        // Act
        var failure = await Should.ThrowAsync<DocumentClientException>(async () => await connection.QueryAsync(
            "INSERT INTO sink (KEY k, VALUE v) SELECT 'new' AS k, {x:ARRAY_AGG(v)} AS v FROM items d UNNEST [1,2] v RETURNING *",
            cancellationToken: harness.Token));
        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05018");
        (await connection.QueryAsync("SELECT COUNT(*) FROM sink", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(0L);
    }
}
