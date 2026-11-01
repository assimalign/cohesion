using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Documents.Tests;

[Collection(nameof(DocumentLikeCancellationCollection))]
public sealed class DocumentGroupMemoryTests
{
    private readonly ITestOutputHelper _output;

    public DocumentGroupMemoryTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Documents] - Group aliases: shared payloads fit a one MiB grant")]
    [InlineData("string")]
    [InlineData("json")]
    [InlineData("view")]
    [InlineData("alternative")]
    [InlineData("alternative-json")]
    [InlineData("aliases")]
    [InlineData("groups")]
    public async Task Execute_SharedGroupAliasPayload_ShouldFitStatementBudgetAsync(string scenario)
    {
        // Arrange: the alternative is equal but independently owned; ninety-nine rows
        // then share it. Multiple terms/groups must also share the parameter payload.
        await using var engine = DocumentDatabaseEngine.Create("shared-group-alias", new() { StatementMemoryBudget = 1024 * 1024 });
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        using var json = JsonDocument.Parse("{\"pad\":\"" + new string('p', 16000) + "\"}");
        byte[] content = scenario == "view" ? Encoding.UTF8.GetBytes("{\"payload\":" + json.RootElement.GetRawText() + "}") : "{}"u8.ToArray();
        await collection.PutAsync(session, "a", content, cancellationToken: CancellationToken.None);
        var parameters = new Dictionary<string, object?>
        {
            ["v"] = scenario is "json" or "alternative-json" ? json.RootElement : new string('p', 16000),
            ["w"] = scenario == "alternative-json" ? json.RootElement.Clone() : new string('p', 16000),
        };
        string terms = scenario switch
        {
            "alternative" or "alternative-json" => "CASE WHEN x = 0 THEN @v ELSE @w END AS key",
            "view" => "payload AS key",
            "aliases" => "@v AS key, @v AS other",
            "groups" => "x AS member, @v AS key",
            _ => "@v AS key",
        };
        string query = "SELECT COUNT(*) FROM items UNNEST [" + string.Join(",", Enumerable.Range(0, 100)) + "] x GROUP BY " + terms;

        // Act
        await using var result = (await session.ExecuteAsync(query, parameters, CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();

        // Assert
        int rows = 0;
        long count = 0;
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { count += row.GetInt64(0); rows++; }
        count.ShouldBe(100L);
        rows.ShouldBe(scenario == "groups" ? 100 : 1);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Group aliases: reservations stay within three times a live payload lower bound")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_SharedGroupAliasReservation_ShouldTrackLiveRepresentationsAsync(bool wrappers)
    {
        // Arrange: a larger grant records the old overcharge as well as the fixed peak.
        await using var engine = DocumentDatabaseEngine.Create("group-alias-reservation", new() { StatementMemoryBudget = 8 * 1024 * 1024 });
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await collection.PutAsync(session, "a", "{}"u8.ToArray(), cancellationToken: CancellationToken.None);
        var parameters = new Dictionary<string, object?> { ["v"] = new string('p', 16000) };
        string query = "SELECT COUNT(*) FROM items UNNEST [" + string.Join(",", Enumerable.Range(0, 100)) + "] x GROUP BY @v";
        long controlPeak = await MeasureAsync(query);

        // Act
        long peak = await MeasureAsync(query + " AS key" + (wrappers ? " GROUP AS g" : ""));

        // Assert: one UTF-16 key is retained. GROUP AS additionally owns one UTF-8
        // copy per wrapper and one per final array member while both coexist.
        // Excluding all metadata makes this stricter than 3x the complete live graph;
        // no process-wide GC delta determines the outcome.
        long livePayload = 16000L * 2 + (wrappers ? 2L * 100 * 16000 : 0);
        _output.WriteLine($"GROUP AS={wrappers}: control peak={controlPeak}; alias peak={peak}; live payload lower bound={livePayload}; bound=3x.");
        peak.ShouldBeLessThanOrEqualTo(3L * livePayload);

        ValueTask<long> MeasureAsync(string text) => database.RunAsync(session, async operation =>
        {
            var memory = operation.GetStatementMemory(CancellationToken.None);
            var request = DocumentQueryRequest.FromOql(text);
            await using (var result = (DatabaseResultSet)await DocumentPlanExecutor.ExecuteAsync(database, operation, request.Statement, parameters, CancellationToken.None))
            {
                await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { row.GetInt64(0).ShouldBe(100L); }
                memory.Bytes.ShouldBe(320L);
            }
            memory.Bytes.ShouldBe(0L);
            return memory.PeakBytes;
        }, CancellationToken.None);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Group aliases: independently owned payloads still exhaust the grant and release")]
    [InlineData("string")]
    [InlineData("json")]
    [InlineData("wrappers")]
    public async Task Execute_OwnedGroupAliasPayload_ShouldRefuseAndReleaseAsync(string scenario)
    {
        // Arrange: equal computed strings are separate allocations; wrappers serialize
        // even a shared string into separate JSON owners and a final array owner.
        var builder = DocumentDatabaseEngine.CreateBuilder("owned-group-alias");
        builder.Options.StatementMemoryBudget = 1024 * 1024;
        builder.Functions.Add(DocumentScalarFunction.Create<decimal, string>("owned_key", _ => new string('p', 16000)));
        using var json = JsonDocument.Parse("{\"pad\":\"" + new string('p', 16000) + "\"}");
        builder.Functions.Add(DocumentScalarFunction.Create<decimal, JsonElement>("owned_json", _ => json.RootElement.Clone()));
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await collection.PutAsync(session, "a", "{}"u8.ToArray(), cancellationToken: CancellationToken.None);
        string query = "SELECT COUNT(*) FROM items UNNEST [" + string.Join(",", Enumerable.Range(0, 100)) + "] x GROUP BY " +
            (scenario == "wrappers" ? "@v AS key GROUP AS g" : scenario == "json" ? "owned_json(x) AS key" : "owned_key(x) AS key");
        var parameters = new Dictionary<string, object?> { ["v"] = new string('p', 16000) };
        var request = DocumentQueryRequest.FromOql(query);

        // Act / Assert
        await database.RunAsync(session, async operation =>
        {
            var memory = operation.GetStatementMemory(CancellationToken.None);
            var failure = await Should.ThrowAsync<DatabaseException>(async () =>
                await DocumentPlanExecutor.ExecuteAsync(database, operation, request.Statement, parameters, CancellationToken.None));
            failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05018");
            memory.Bytes.ShouldBe(0L);
            return true;
        }, CancellationToken.None);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - GROUP AS: wrapper retention exhausts the shared grant after ordinary grouping fits")]
    public async Task Execute_GroupWrapperBudget_ShouldRefuseAndReleaseAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("wrapper-budget", new() { StatementMemoryBudget = 8192 });
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await collection.PutAsync(session, "a", Encoding.UTF8.GetBytes("{\"pad\":\"" + new string('p', 1200) + "\"}"), cancellationToken: CancellationToken.None);
        await using var control = (await session.ExecuteAsync("SELECT COUNT(*) FROM items UNNEST [1,2,3,4] x GROUP BY TRUE", cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        var request = DocumentQueryRequest.FromOql("SELECT ARRAY_LENGTH(g) FROM items UNNEST [1,2,3,4] x GROUP BY TRUE GROUP AS g");

        // Act / Assert
        await database.RunAsync(session, async operation =>
        {
            var memory = operation.GetStatementMemory(CancellationToken.None);
            var failure = await Should.ThrowAsync<DatabaseException>(async () =>
                await DocumentPlanExecutor.ExecuteAsync(database, operation, request.Statement, null, CancellationToken.None));
            failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05018");
            failure.Diagnostics[0].Start.ShouldNotBeNull();
            memory.Bytes.ShouldBe(0L);
            return true;
        }, CancellationToken.None);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - GROUP AS: reservations release on success projection failure and deterministic cancellation")]
    [InlineData("success")]
    [InlineData("projection")]
    [InlineData("cancel")]
    public async Task Execute_GroupWrapperLifetime_ShouldReleaseOnEveryExitAsync(string scenario)
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        await using var engine = DocumentDatabaseEngine.Create("wrapper-lifetime", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await collection.PutAsync(session, "a", Encoding.UTF8.GetBytes("{\"pad\":\"" + new string('p', 1200) + "\"}"), cancellationToken: CancellationToken.None);
        long controlPeak = 0;
        var controlRequest = DocumentQueryRequest.FromOql("SELECT COUNT(*) FROM items GROUP BY TRUE");
        await database.RunAsync(session, async operation =>
        {
            await using var result = (DatabaseResultSet)await DocumentPlanExecutor.ExecuteAsync(database, operation, controlRequest.Statement, null, CancellationToken.None);
            controlPeak = operation.GetStatementMemory(CancellationToken.None).PeakBytes;
            return true;
        }, CancellationToken.None);
        string projection = scenario == "projection" ? "1/0" : "ARRAY_LENGTH(g)";
        var request = DocumentQueryRequest.FromOql("SELECT " + projection + " FROM items GROUP BY TRUE GROUP AS g");

        // Act / Assert
        await database.RunAsync(session, async operation =>
        {
            var memory = operation.GetStatementMemory(cancellation.Token);
            if (scenario == "cancel")
            { memory.RetentionObserved = () => { if (memory.Bytes > controlPeak + 128) { cancellation.Cancel(); } }; }
            if (scenario == "success")
            {
                await using var result = (await DocumentPlanExecutor.ExecuteAsync(database, operation, request.Statement, null, cancellation.Token)).ShouldBeAssignableTo<DatabaseResultSet>();
                memory.Bytes.ShouldBe(320L);
                await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { row.GetValue(0).ShouldBe(1L); }
            }
            else if (scenario == "cancel")
            {
                await Should.ThrowAsync<OperationCanceledException>(async () =>
                    await DocumentPlanExecutor.ExecuteAsync(database, operation, request.Statement, null, cancellation.Token));
                cancellation.IsCancellationRequested.ShouldBeTrue();
            }
            else
            {
                (await Should.ThrowAsync<DatabaseException>(async () =>
                    await DocumentPlanExecutor.ExecuteAsync(database, operation, request.Statement, null, cancellation.Token)))
                    .Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD02006");
            }
            memory.Bytes.ShouldBe(0L);
            return true;
        }, CancellationToken.None);
    }
}
