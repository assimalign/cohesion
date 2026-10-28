using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Internal;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentDmlMemoryTests
{
    [Theory(DisplayName = "Cohesion Test [Documents] - DML: pending buffers refuse before writing and release their reservations (#1755)")]
    [InlineData("UPDATE items SET pad = @v")]
    [InlineData("DELETE FROM items")]
    [InlineData("UPSERT INTO items (KEY, VALUE) VALUES ('a',@v),('b',@v)")]
    [InlineData("INSERT INTO items (KEY, VALUE) VALUES ('a',@v),('b',@v)")]
    public async Task Execute_PendingBudget_ShouldRefuseBeforeAnyWriteAsync(string query)
    {
        // Arrange: direct collection writes do not stage an OQL pending buffer.
        await using var engine = DocumentDatabaseEngine.Create("dml-budget", new() { StatementMemoryBudget = 1024 });
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        string first = new('a', 400), second = new('b', 400);
        await collection.PutAsync(session, new(first), "{}"u8.ToArray(), cancellationToken: CancellationToken.None);
        await collection.PutAsync(session, new(second), "{}"u8.ToArray(), cancellationToken: CancellationToken.None);
        var request = DocumentQueryRequest.FromOql(query);
        var parameters = new Dictionary<string, object?> { ["v"] = new string('x', 600) };

        // Act / Assert: inspect inside the operation, before its cleanup clears the counter.
        await database.RunAsync(session, async operation =>
        {
            var memory = operation.GetStatementMemory(CancellationToken.None);
            var failure = await Should.ThrowAsync<DatabaseException>(async () =>
                await DocumentPlanExecutor.ExecuteAsync(database, operation, request.Statement, parameters, CancellationToken.None));
            failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05018");
            memory.Bytes.ShouldBe(0L);
            var metadata = database.Catalog.FindCollection("items", operation.Context.Snapshot)!.Value;
            var entries = database.Catalog.GetDocuments(metadata.Id, null, operation.Context.Snapshot);
            entries.Count.ShouldBe(2);
            foreach (var entry in entries) { Encoding.UTF8.GetString(database.ReadDocument(entry).Content.Span).ShouldBe("{}"); }
            return true;
        }, CancellationToken.None);
        (await collection.GetAsync(session, new(first), CancellationToken.None)).ShouldNotBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - DML: VALUES staging releases on success duplicate and cancellation (#1755)")]
    [InlineData("success")]
    [InlineData("duplicate")]
    [InlineData("cancel")]
    public async Task Execute_PendingLifetime_ShouldReleaseOnEveryExitAsync(string scenario)
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        // A duplicate retains no second key and must keep its duplicate diagnostic
        // even when the first pending document has almost consumed the grant.
        await using var engine = DocumentDatabaseEngine.Create("dml-lifetime", new()
        { StatementMemoryBudget = scenario == "duplicate" ? 240 : 64L * 1024 * 1024 });
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        string key = scenario == "duplicate" ? "a" : "b";
        var request = DocumentQueryRequest.FromOql("UPSERT INTO items (KEY, VALUE) VALUES ('a',{}),('" + key + "',{})");

        // Act / Assert
        await database.RunAsync(session, async operation =>
        {
            var memory = operation.GetStatementMemory(cancellation.Token);
            if (scenario == "cancel") { memory.RetentionObserved = () => cancellation.Cancel(); }
            if (scenario == "success")
            {
                (await DocumentPlanExecutor.ExecuteAsync(database, operation, request.Statement, null, cancellation.Token)).AffectedCount.ShouldBe(2L);
            }
            else if (scenario == "duplicate")
            {
                await Should.ThrowAsync<DocumentDuplicateKeyException>(async () =>
                    await DocumentPlanExecutor.ExecuteAsync(database, operation, request.Statement, null, cancellation.Token));
            }
            else
            {
                await Should.ThrowAsync<OperationCanceledException>(async () =>
                    await DocumentPlanExecutor.ExecuteAsync(database, operation, request.Statement, null, cancellation.Token));
            }
            memory.Bytes.ShouldBe(0L);
            return true;
        }, CancellationToken.None);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - DML: INSERT and UPSERT SELECT share source and pending reservations (#1755)")]
    [InlineData("INSERT")]
    [InlineData("UPSERT")]
    public async Task Execute_SelectPendingBudget_ShouldRefuseBeforeAnyWriteAsync(string verb)
    {
        // Arrange: the SELECT fits by itself; its live results and pending writes do not.
        await using var engine = DocumentDatabaseEngine.Create("select-dml-budget", new() { StatementMemoryBudget = 12_000 });
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var source = await session.CreateCollectionAsync("source", CancellationToken.None);
        await session.CreateCollectionAsync("target", CancellationToken.None);
        foreach (string id in new[] { "a", "b", "c" })
        { await source.PutAsync(session, new(id), "{}"u8.ToArray(), cancellationToken: CancellationToken.None); }
        using var json = JsonDocument.Parse("{\"pad\":\"" + new string('p', 2000) + "\"}");
        var parameters = new Dictionary<string, object?> { ["v"] = json.RootElement };
        var request = DocumentQueryRequest.FromOql(verb + " INTO target (KEY k, VALUE v) SELECT META().id AS k, @v AS v FROM source");

        // Act / Assert
        await database.RunAsync(session, async operation =>
        {
            var memory = operation.GetStatementMemory(CancellationToken.None);
            var failure = await Should.ThrowAsync<DatabaseException>(async () =>
                await DocumentPlanExecutor.ExecuteAsync(database, operation, request.Statement, parameters, CancellationToken.None));
            failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05018");
            memory.Bytes.ShouldBe(0L);
            var metadata = database.Catalog.FindCollection("target", operation.Context.Snapshot)!.Value;
            database.Catalog.GetDocuments(metadata.Id, null, operation.Context.Snapshot).ShouldBeEmpty();
            return true;
        }, CancellationToken.None);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - DML: UPDATE and DELETE pending reservations release on success and cancellation (#1755)")]
    [InlineData("UPDATE items SET n = 1", false)]
    [InlineData("DELETE FROM items", false)]
    [InlineData("UPDATE items SET n = 1", true)]
    [InlineData("DELETE FROM items", true)]
    public async Task Execute_MutationPendingLifetime_ShouldReleaseOnEveryExitAsync(string query, bool cancel)
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        await using var engine = DocumentDatabaseEngine.Create("mutation-lifetime", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        foreach (string id in new[] { "a", "b" })
        { await collection.PutAsync(session, new(id), "{}"u8.ToArray(), cancellationToken: CancellationToken.None); }
        var request = DocumentQueryRequest.FromOql(query);

        // Act / Assert
        await database.RunAsync(session, async operation =>
        {
            var memory = operation.GetStatementMemory(cancellation.Token);
            if (cancel) { memory.RetentionObserved = () => cancellation.Cancel(); }
            if (cancel)
            {
                await Should.ThrowAsync<OperationCanceledException>(async () =>
                    await DocumentPlanExecutor.ExecuteAsync(database, operation, request.Statement, null, cancellation.Token));
            }
            else { (await DocumentPlanExecutor.ExecuteAsync(database, operation, request.Statement, null, cancellation.Token)).AffectedCount.ShouldBe(2L); }
            memory.Bytes.ShouldBe(0L);
            return true;
        }, CancellationToken.None);
    }
}
