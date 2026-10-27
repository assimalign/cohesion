using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed partial class DocumentRegisteredFunctionTests
{
    [Fact(DisplayName = "Cohesion Test [Documents] - Functions: typed JSON scalars accept arrays and objects while incompatible stored values yield NULL")]
    public async Task Execute_TypedJsonScalar_ShouldAdmitOnlyCompositeInputsAsync()
    {
        int calls = 0;
        await using var engine = await BuildAsync(DocumentScalarFunction.Create<JsonElement, long>("json_size", value =>
        {
            calls++;
            return value.ValueKind == JsonValueKind.Array ? value.GetArrayLength() : value.GetPropertyCount();
        }));
        var database = await engine.CreateDatabaseAsync("json_scalar", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await collection.PutAsync(session, "a", "{\"value\":[1,2]}"u8.ToArray(), cancellationToken: CancellationToken.None);
        await collection.PutAsync(session, "b", "{\"value\":{\"one\":1}}"u8.ToArray(), cancellationToken: CancellationToken.None);
        await collection.PutAsync(session, "c", "{\"value\":\"text\"}"u8.ToArray(), cancellationToken: CancellationToken.None);
        await collection.PutAsync(session, "d", "{\"value\":1}"u8.ToArray(), cancellationToken: CancellationToken.None);
        await collection.PutAsync(session, "e", "{\"value\":true}"u8.ToArray(), cancellationToken: CancellationToken.None);
        await collection.PutAsync(session, "f", "{\"value\":null}"u8.ToArray(), cancellationToken: CancellationToken.None);
        await collection.PutAsync(session, "g", "{}"u8.ToArray(), cancellationToken: CancellationToken.None);

        var rows = await RowsAsync(session, DocumentQueryRequest.FromOql("SELECT json_size(d.value) AS size FROM items d"));
        rows.Count.ShouldBe(7);
        rows[0].GetValue(0).ShouldBe(2L);
        rows[1].GetValue(0).ShouldBe(1L);
        foreach (var row in rows.Skip(2).Take(4)) { row.IsNull(0).ShouldBeTrue(); }
        rows[6].IsMissing(0).ShouldBeTrue();
        calls.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Functions: typed JSON aggregates skip incompatible stored values before delegates")]
    public async Task Execute_TypedJsonAggregate_ShouldSkipNonCompositeInputsAsync()
    {
        int calls = 0;
        await using var engine = await BuildAsync(DocumentAggregateFunction.Create<long, JsonElement, long>("json_total", () => 0L,
            (state, value) =>
            {
                calls++;
                return state + (value.ValueKind == JsonValueKind.Array ? value.GetArrayLength() : value.GetPropertyCount());
            }, state => state));
        var database = await engine.CreateDatabaseAsync("json_aggregate", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await collection.PutAsync(session, "a", "{\"value\":[1,2]}"u8.ToArray(), cancellationToken: CancellationToken.None);
        await collection.PutAsync(session, "b", "{\"value\":{\"one\":1}}"u8.ToArray(), cancellationToken: CancellationToken.None);
        await collection.PutAsync(session, "c", "{\"value\":\"text\"}"u8.ToArray(), cancellationToken: CancellationToken.None);
        await collection.PutAsync(session, "d", "{\"value\":null}"u8.ToArray(), cancellationToken: CancellationToken.None);
        await collection.PutAsync(session, "e", "{}"u8.ToArray(), cancellationToken: CancellationToken.None);

        var row = (await RowsAsync(session, DocumentQueryRequest.FromOql("SELECT json_total(d.value) AS total FROM items d"))).ShouldHaveSingleItem();
        row.GetValue(0).ShouldBe(3L);
        calls.ShouldBe(2);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Functions: incompatible typed JSON literals fail binding over an empty collection")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Plan_TypedJsonLiteral_ShouldRefuseBeforeDataAsync(bool aggregate)
    {
        int calls = 0;
        DocumentFunction function = aggregate
            ? DocumentAggregateFunction.Create<long, JsonElement, long>("json_input", () => 0L, (state, value) => { calls++; return state + 1; }, state => state)
            : DocumentScalarFunction.Create<JsonElement, long>("json_input", value => { calls++; return value.GetArrayLength(); });
        await using var engine = await BuildAsync(function);
        var database = await engine.CreateDatabaseAsync("json_literal", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);

        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT json_input('wrong') FROM items", cancellationToken: CancellationToken.None));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05001");
        session.LastQueryDocumentsRead.ShouldBe(0);
        calls.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Functions: typed integer conversion faults retain data codes without invoking delegates")]
    [InlineData(false, "0.5", "CD02008")]
    [InlineData(true, "0.5", "CD02008")]
    [InlineData(false, "9223372036854775808", "CD02007")]
    [InlineData(true, "9223372036854775808", "CD02007")]
    public async Task Execute_TypedIntegerConversion_ShouldKeepDataErrorCodesAsync(bool aggregate, string literal, string code)
    {
        int calls = 0;
        DocumentFunction function = aggregate
            ? DocumentAggregateFunction.Create<long, long, long>("integer_input", () => 0L, (state, value) => { calls++; return state + value; }, state => state)
            : DocumentScalarFunction.Create<long, long>("integer_input", value => { calls++; return value; });
        await using var engine = await BuildAsync(function);
        var database = await engine.CreateDatabaseAsync("numeric_conversion", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);

        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT integer_input(" + literal + ") FROM items", cancellationToken: CancellationToken.None));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
        var cause = error.InnerException.ShouldNotBeNull();
        cause.GetType().ShouldBe(code == "CD02007" ? typeof(OverflowException) : typeof(InvalidCastException));
        calls.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Functions: an actual callback overflow remains an external fault")]
    public async Task Execute_CallbackOverflow_ShouldRetainExternalFaultCodeAsync()
    {
        var cause = new OverflowException("application callback overflow");
        await using var engine = await BuildAsync(DocumentScalarFunction.Create<decimal, decimal>("callback_overflow", _ => throw cause));
        var database = await engine.CreateDatabaseAsync("callback_conversion", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT callback_overflow(amount) FROM items", cancellationToken: CancellationToken.None));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05017");
        error.InnerException.ShouldBeSameAs(cause);
    }
}
