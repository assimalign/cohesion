using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentEngineTests
{
    [Fact]
    public async Task Nested_arrays_scalars_and_mixed_shapes_round_trip_without_changing_bytes()
    {
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = (IDocumentDatabase)await engine.CreateDatabaseAsync("test");
        var collection = await database.CreateCollectionAsync("items");
        await using var session = await database.CreateSessionAsync();
        string[] values = ["{\"nested\":{\"array\":[1,null,{\"name\":\"雪\"}]}}", "{\"other\":true}", "[1,2,3]", "null", "42", "\"scalar\""];
        for (int i = 0; i < values.Length; i++)
        {
            var bytes = Encoding.UTF8.GetBytes(values[i]);
            var saved = await collection.PutAsync(session, i.ToString(), bytes);
            var read = (await collection.GetAsync(session, i.ToString())).ShouldNotBeNull();
            read.Content.ToArray().ShouldBe(bytes);
            read.Version.ShouldBe(saved.Version);
        }
    }

    [Fact]
    public async Task Versions_are_conditional_and_never_reused_after_delete()
    {
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = (IDocumentDatabase)await engine.CreateDatabaseAsync("test");
        var collection = await database.CreateCollectionAsync("items");
        await using var session = await database.CreateSessionAsync();
        var first = await collection.PutAsync(session, "one", "{}"u8.ToArray());
        var second = await collection.PutAsync(session, "one", "{\"a\":1}"u8.ToArray(), first.Version);
        second.Version.Value.ShouldBeGreaterThan(first.Version.Value);
        await Should.ThrowAsync<DatabaseException>(async () => await collection.PutAsync(session, "one", "null"u8.ToArray(), first.Version));
        await Should.ThrowAsync<DatabaseException>(async () => await collection.DeleteAsync(session, "one", first.Version));
        (await collection.GetAsync(session, "one")).ShouldNotBeNull().Version.ShouldBe(second.Version);
        (await collection.DeleteAsync(session, "one", second.Version)).ShouldBeTrue();
        (await collection.DeleteAsync(session, "one")).ShouldBeFalse();
        var third = await collection.PutAsync(session, "one", "[]"u8.ToArray());
        third.Version.Value.ShouldBeGreaterThan(second.Version.Value);
    }

    [Theory]
    [InlineData(IsolationLevel.Snapshot, 1)]
    [InlineData(IsolationLevel.ReadCommitted, 2)]
    public async Task Explicit_transactions_have_the_requested_visibility(IsolationLevel isolation, int expected)
    {
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = (IDocumentDatabase)await engine.CreateDatabaseAsync("test");
        var collection = await database.CreateCollectionAsync("items");
        await using var writer = await database.CreateSessionAsync();
        await using var reader = await database.CreateSessionAsync();
        await collection.PutAsync(writer, "one", "1"u8.ToArray());
        await using var transaction = await reader.BeginTransactionAsync(isolation);
        await collection.PutAsync(writer, "one", "2"u8.ToArray());
        Encoding.UTF8.GetString((await collection.GetAsync(reader, "one")).ShouldNotBeNull().Content.Span).ShouldBe(expected.ToString());
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Explicit_rollback_and_session_disposal_undo_all_document_mutations()
    {
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = (IDocumentDatabase)await engine.CreateDatabaseAsync("test");
        var collection = await database.CreateCollectionAsync("items");
        await using var session = await database.CreateSessionAsync();
        await collection.PutAsync(session, "old", "1"u8.ToArray());
        await using var observer = await database.CreateSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        await collection.DeleteAsync(session, "old");
        await collection.PutAsync(session, "new", "2"u8.ToArray());
        (await collection.GetAsync(session, "old")).ShouldBeNull();
        (await collection.GetAsync(observer, "new")).ShouldBeNull();
        await session.DisposeAsync();
        transaction.State.ShouldBe(TransactionState.RolledBack);
        (await collection.GetAsync(observer, "old")).ShouldNotBeNull();
        (await collection.GetAsync(observer, "new")).ShouldBeNull();
    }

    [Fact]
    public async Task Snapshot_write_conflicts_abort_and_do_not_overwrite_newer_content()
    {
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = (IDocumentDatabase)await engine.CreateDatabaseAsync("test");
        var collection = await database.CreateCollectionAsync("items");
        await using var old = await database.CreateSessionAsync();
        await using var current = await database.CreateSessionAsync();
        await collection.PutAsync(current, "one", "1"u8.ToArray());
        await using var transaction = await old.BeginTransactionAsync();
        await collection.PutAsync(current, "one", "2"u8.ToArray());
        await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () => await collection.PutAsync(old, "one", "3"u8.ToArray()));
        transaction.State.ShouldBe(TransactionState.RolledBack);
        Encoding.UTF8.GetString((await collection.GetAsync(current, "one")).ShouldNotBeNull().Content.Span).ShouldBe("2");
    }

    [Fact]
    public async Task Schema_owned_collection_refuses_drop_and_index_alter_and_adhoc_is_mutable()
    {
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = (DocumentDatabaseInstance)await engine.CreateDatabaseAsync("test");
        await database.CreateCollectionAsync("owned");
        var context = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        var metadata = database.Catalog.FindCollection("owned", context.Snapshot).ShouldNotBeNull();
        await database.Catalog.SaveCollectionAsync(metadata with { Owner = DatabaseObjectOwner.Schema, OwningSchema = "Sales" }, context);
        await database.Coordinator.CommitAsync(context);
        await using var session = await database.CreateSessionAsync();
        var scoped = (IDocumentDatabase)session.Database;
        var error = await Should.ThrowAsync<DatabaseObjectLockedException>(async () => await scoped.DropCollectionAsync("owned"));
        error.Message.ShouldContain("owned");
        error.Message.ShouldContain("Sales");
        error.Message.ShouldContain("DROP COLLECTION");
        await Should.ThrowAsync<DatabaseObjectLockedException>(async () => await scoped.CreateIndexAsync("owned", "ix", "a"));
        await Should.ThrowAsync<DatabaseObjectLockedException>(async () => await scoped.DropIndexAsync("owned", "ix"));
        await scoped.CreateCollectionAsync("adhoc");
        await scoped.CreateIndexAsync("adhoc", "ix", "a");
        await scoped.DropIndexAsync("adhoc", "ix");
        await scoped.DropCollectionAsync("adhoc");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transactions_older_than_index_ddl_cannot_mutate_or_drop_the_collection(bool drop)
    {
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = (IDocumentDatabase)await engine.CreateDatabaseAsync("test");
        var collection = await database.CreateCollectionAsync("items");
        await using var session = await database.CreateSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        await database.CreateIndexAsync("items", "ix", "a");
        if (drop)
        {
            await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () => await ((IDocumentDatabase)session.Database).DropCollectionAsync("items"));
        }
        else
        {
            await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () => await collection.PutAsync(session, "one", "{\"a\":1}"u8.ToArray()));
        }
        transaction.State.ShouldBe(TransactionState.RolledBack);
        (await database.GetCollectionAsync("items")).Name.ShouldBe("items");
    }

    [Fact]
    public async Task Engine_workers_enumeration_durable_reopen_and_idempotent_disposal()
    {
        string root = Path.Combine(Path.GetTempPath(), "cohesion-documents-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var engine = DocumentDatabaseEngine.Create(new() { RootPath = root, Durability = StorageCommitDurability.Grouped });
            engine.State.ShouldBe(EngineState.Running);
            engine.Workers.Select(worker => worker.Kind).Distinct().Count().ShouldBe(4);
            var database = (IDocumentDatabase)await engine.CreateDatabaseAsync("saved");
            var collection = await database.CreateCollectionAsync("items");
            var bytes = Encoding.UTF8.GetBytes("{\"large\":\"" + new string('x', 50000) + "\"}");
            await using (var session = await database.CreateSessionAsync()) { await collection.PutAsync(session, "large", bytes); }
            engine.Dispose();
            await engine.DisposeAsync();
            engine.State.ShouldBe(EngineState.Disposed);
            await using var reopened = DocumentDatabaseEngine.Create(new() { RootPath = root });
            var names = new List<string>();
            await foreach (var item in reopened.GetDatabasesAsync()) { names.Add(item.Name.ToString()); }
            names.ShouldBe(["saved"]);
            reopened.TryGetDatabase("SAVED", out var stored).ShouldBeTrue();
            await using var reader = await stored.CreateSessionAsync();
            var reopenedCollection = await ((IDocumentDatabase)stored).GetCollectionAsync("items");
            (await reopenedCollection.GetAsync(reader, "large")).ShouldNotBeNull().Content.ToArray().ShouldBe(bytes);
            await reader.DisposeAsync();
            await reopened.DropDatabaseAsync("saved");
            reopened.TryGetDatabase("saved", out _).ShouldBeFalse();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("..")]
    [InlineData("x/y")]
    [InlineData("x\\y")]
    public async Task Database_names_cannot_escape_the_engine_directory(string name)
    {
        await using var engine = DocumentDatabaseEngine.Create(new());
        await Should.ThrowAsync<ArgumentException>(async () => await engine.CreateDatabaseAsync(name));
    }
}
