using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Indexing.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
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
        var database = await engine.CreateDatabaseAsync("test");
        await using var session = await database.CreateSessionAsync();
        var collection = await session.CreateCollectionAsync("items");
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
        var database = await engine.CreateDatabaseAsync("test");
        await using var session = await database.CreateSessionAsync();
        var collection = await session.CreateCollectionAsync("items");
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
        var database = await engine.CreateDatabaseAsync("test");
        await using var writer = await database.CreateSessionAsync();
        var collection = await writer.CreateCollectionAsync("items");
        await using var reader = await database.CreateSessionAsync();
        var read = await reader.GetCollectionAsync("items");
        await collection.PutAsync(writer, "one", "1"u8.ToArray());
        await using var transaction = await reader.BeginTransactionAsync(isolation);
        await collection.PutAsync(writer, "one", "2"u8.ToArray());
        Encoding.UTF8.GetString((await read.GetAsync(reader, "one")).ShouldNotBeNull().Content.Span).ShouldBe(expected.ToString());
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Explicit_rollback_and_session_disposal_undo_all_document_mutations()
    {
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("test");
        await using var session = await database.CreateSessionAsync();
        var collection = await session.CreateCollectionAsync("items");
        await collection.PutAsync(session, "old", "1"u8.ToArray());
        await using var observer = await database.CreateSessionAsync();
        var observed = await observer.GetCollectionAsync("items");
        await using var transaction = await session.BeginTransactionAsync();
        await collection.DeleteAsync(session, "old");
        await collection.PutAsync(session, "new", "2"u8.ToArray());
        (await collection.GetAsync(session, "old")).ShouldBeNull();
        (await observed.GetAsync(observer, "new")).ShouldBeNull();
        await session.DisposeAsync();
        transaction.State.ShouldBe(TransactionState.RolledBack);
        (await observed.GetAsync(observer, "old")).ShouldNotBeNull();
        (await observed.GetAsync(observer, "new")).ShouldBeNull();
    }

    /// <summary>
    /// The token is observed only before the rollback starts (#1226), so nothing of a rollback
    /// canceled by then ran, and no purge pass may undo the still-active writer.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Rollback: a token canceled before the rollback starts leaves the transaction and its writes intact")]
    public async Task RollbackAsync_TokenCanceledBeforeStart_ShouldLeaveTransactionAndItsWritesIntact()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("test");
        await using var session = await database.CreateSessionAsync();
        var collection = await session.CreateCollectionAsync("items");
        var transaction = await session.BeginTransactionAsync();
        await collection.PutAsync(session, "kept", "1"u8.ToArray());
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        // Act
        await Should.ThrowAsync<OperationCanceledException>(async () => await transaction.RollbackAsync(canceled.Token));
        var stateAfterRollback = transaction.State;
        int pendingAfterRollback = database.Coordinator.VersionStore.PendingAbortedPurges.Count;
        database.Coordinator.RunVersionPurgePass(default);
        await transaction.CommitAsync();

        // Assert
        stateAfterRollback.ShouldBe(TransactionState.Active);
        pendingAfterRollback.ShouldBe(0);
        transaction.State.ShouldBe(TransactionState.Committed);
        (await collection.GetAsync(session, "kept")).ShouldNotBeNull();
    }

    [Fact]
    public async Task Snapshot_write_conflicts_abort_and_do_not_overwrite_newer_content()
    {
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("test");
        await using var old = await database.CreateSessionAsync();
        var collection = await old.CreateCollectionAsync("items");
        await using var current = await database.CreateSessionAsync();
        var currentItems = await current.GetCollectionAsync("items");
        await currentItems.PutAsync(current, "one", "1"u8.ToArray());
        await using var transaction = await old.BeginTransactionAsync();
        await currentItems.PutAsync(current, "one", "2"u8.ToArray());
        await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () => await collection.PutAsync(old, "one", "3"u8.ToArray()));
        // The conflict aborts the explicit transaction, which waits for the caller's rollback (#1225).
        transaction.State.ShouldBe(TransactionState.Faulted);
        await transaction.RollbackAsync();
        transaction.State.ShouldBe(TransactionState.RolledBack);
        Encoding.UTF8.GetString((await currentItems.GetAsync(current, "one")).ShouldNotBeNull().Content.Span).ShouldBe("2");
    }

    [Fact]
    public async Task Schema_owned_collection_refuses_drop_and_index_ddl_and_adhoc_is_mutable()
    {
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("test");
        await using (var setup = await database.CreateSessionAsync())
        {
            await setup.CreateCollectionAsync("owned");
        }

        var context = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        var metadata = database.Catalog.FindCollection("owned", context.Snapshot).ShouldNotBeNull();
        await database.Catalog.SaveCollectionAsync(metadata with { Owner = DatabaseObjectOwner.Schema, OwningSchema = "Sales" }, context);
        await database.Coordinator.CommitAsync(context);
        await using var session = await database.CreateSessionAsync();
        var error = await Should.ThrowAsync<DatabaseObjectLockedException>(async () => await session.DropCollectionAsync("owned"));
        error.Message.ShouldContain("owned");
        error.Message.ShouldContain("Sales");
        error.Message.ShouldContain("DROP COLLECTION");
        var createError = await Should.ThrowAsync<DatabaseObjectLockedException>(async () =>
            await session.ExecuteAsync("CREATE INDEX ix ON owned (a)"));
        createError.Message.ShouldContain("owned");
        createError.Message.ShouldContain("Sales");
        createError.Message.ShouldContain("CREATE INDEX");
        var dropError = await Should.ThrowAsync<DatabaseObjectLockedException>(async () =>
            await session.ExecuteAsync("DROP INDEX ix ON owned"));
        dropError.Message.ShouldContain("owned");
        dropError.Message.ShouldContain("Sales");
        dropError.Message.ShouldContain("DROP INDEX");
        await session.CreateCollectionAsync("adhoc");
        await session.ExecuteAsync("CREATE INDEX ix ON adhoc (a)");
        await session.ExecuteAsync("DROP INDEX ix ON adhoc");
        await session.DropCollectionAsync("adhoc");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transactions_older_than_index_ddl_cannot_mutate_or_drop_the_collection(bool drop)
    {
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("test");
        await using var session = await database.CreateSessionAsync();
        var collection = await session.CreateCollectionAsync("items");
        await using var transaction = await session.BeginTransactionAsync();
        await using var ddlSession = await database.CreateSessionAsync();
        await ddlSession.ExecuteAsync("CREATE INDEX ix ON items (a)");
        if (drop)
        {
            await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () => await session.DropCollectionAsync("items"));
        }
        else
        {
            await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () => await collection.PutAsync(session, "one", "{\"a\":1}"u8.ToArray()));
        }
        // The conflict aborts the explicit transaction, which waits for the caller's rollback (#1225).
        transaction.State.ShouldBe(TransactionState.Faulted);
        await transaction.RollbackAsync();
        transaction.State.ShouldBe(TransactionState.RolledBack);
        (await session.GetCollectionAsync("items")).Name.ShouldBe("items");
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
            var database = await engine.CreateDatabaseAsync("saved");
            var bytes = Encoding.UTF8.GetBytes("{\"large\":\"" + new string('x', 50000) + "\"}");
            await using (var session = await database.CreateSessionAsync())
            {
                var collection = await session.CreateCollectionAsync("items");
                await collection.PutAsync(session, "large", bytes);
            }
            engine.Dispose();
            await engine.DisposeAsync();
            engine.State.ShouldBe(EngineState.Disposed);
            await using var reopened = DocumentDatabaseEngine.Create(new() { RootPath = root });
            var names = new List<string>();
            await foreach (var item in reopened.GetDatabasesAsync()) { names.Add(item.Name.ToString()); }
            names.ShouldBe(["saved"]);
            reopened.TryGetDatabase("SAVED", out var stored).ShouldBeTrue();
            await using var reader = await stored.CreateSessionAsync();
            var reopenedCollection = await reader.GetCollectionAsync("items");
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

    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Format: a database whose indexes are in B-tree page format 1 is refused at open with COHDBI001, its files untouched (#1194)")]
    public async Task Open_IndexPagesInFormatOne_ShouldBeRefused()
    {
        string root = Path.Combine(Path.GetTempPath(), "cohesion-documents-format-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // Arrange: a closed database with an index, its index pages rewritten into
            // the layout engines before #1194 wrote (entries ordered by key alone).
            await using (var engine = DocumentDatabaseEngine.Create(new() { RootPath = root }))
            {
                var database = await engine.CreateDatabaseAsync("legacy");
                await using var session = await database.CreateSessionAsync();
                var collection = await session.CreateCollectionAsync("items");
                await collection.PutAsync(session, "a", Encoding.UTF8.GetBytes("{\"score\":1}"));
                await session.ExecuteAsync("CREATE INDEX by_score ON items (score)");
            }

            LegacyBTreePages.DowngradeDataFiles(root).ShouldBeGreaterThan(0);
            var before = Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);

            // Act
            await using var reopened = DocumentDatabaseEngine.Create(new() { RootPath = root });
            var failure = await Should.ThrowAsync<DatabaseException>(async () => await reopened.OpenDatabaseAsync("legacy"));

            // Assert: the coded refusal, with the index manager's as its cause; the
            // check ran before recovery, so the database is left as it was.
            failure.Message.ShouldStartWith("Database 'legacy' cannot be opened. " + IndexFormatException.ErrorCode + ": ", Case.Sensitive);
            failure.Message.ShouldContain("uses B-tree page format 1, but this engine supports only format 2", Case.Sensitive);
            failure.InnerException.ShouldBeOfType<IndexFormatException>().FoundVersion.ShouldBe(1);
            reopened.TryGetDatabase("legacy", out _).ShouldBeFalse();
            await reopened.DisposeAsync();

            foreach (var (path, bytes) in before)
            {
                File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes).ShouldBeTrue($"{path} was modified by the refused open");
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Format: a database in storage format 2 (the format before #1253) is refused at open with COHDBS001 naming it, its files untouched (#1251, #1253)")]
    public async Task Open_StorageFormatTwo_ShouldBeRefusedNamingTheDatabase()
    {
        string root = Path.Combine(Path.GetTempPath(), "cohesion-documents-storage-format-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // Arrange: a closed database whose page 0 names storage format 2, the format before #1253.
            await using (var engine = DocumentDatabaseEngine.Create(new() { RootPath = root }))
            {
                var database = await engine.CreateDatabaseAsync("legacy");
                await using var session = await database.CreateSessionAsync();
                var collection = await session.CreateCollectionAsync("items");
                await collection.PutAsync(session, "a", Encoding.UTF8.GetBytes("{\"score\":1}"));
            }

            StorageFormatFiles.WriteVersion(Path.Combine(root, "legacy", "document.dat"), version: 2);
            var before = Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);

            // Act
            await using var reopened = DocumentDatabaseEngine.Create(new() { RootPath = root });
            var failure = await Should.ThrowAsync<DatabaseException>(async () => await reopened.OpenDatabaseAsync("legacy"));

            // Assert: the storage's coded refusal, named for the database; nothing written.
            failure.Message.ShouldStartWith("Database 'legacy' cannot be opened. " + StorageFormatException.ErrorCode + ": ", Case.Sensitive);
            failure.Message.ShouldContain("uses storage format 2, but this engine supports only storage format 3", Case.Sensitive);
            failure.InnerException.ShouldBeOfType<StorageFormatException>().FoundVersion.ShouldBe(2);
            reopened.TryGetDatabase("legacy", out _).ShouldBeFalse();
            await reopened.DisposeAsync();

            Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length.ShouldBe(before.Count);
            foreach (var (path, bytes) in before)
            {
                File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes).ShouldBeTrue($"{path} was modified by the refused open");
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>
    /// The engine's lookup is typed (concrete-types plan, §6.5): an <c>out var</c> call binds the
    /// typed overload, and an explicitly typed base <c>out</c> still binds the base's lookup.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Engine: the typed lookup binds an out-var call, and a base-typed out binds the base's")]
    public async Task TryGetDatabase_OutVarAndBaseTypedOut_ShouldBindTheTypedAndTheBaseLookups()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("test");

        // Act
        bool typedFound = engine.TryGetDatabase("test", out var typed);
        DocumentDatabase? lookedUp = typed;
        bool baseFound = engine.TryGetDatabase("test", out DatabaseInstance? untyped);

        // Assert
        typedFound.ShouldBeTrue();
        lookedUp.ShouldBeSameAs(database);
        baseFound.ShouldBeTrue();
        untyped.ShouldBeSameAs(database);
        database.Engine.ShouldBeSameAs(engine);
    }

    /// <summary>
    /// The root engine base's guards (concrete-types plan §6.4, the engine's guards): every member
    /// checks an empty name, then disposal, then the token, and the enumeration checks disposal
    /// when it is called. The document engine's own name rule (a single file-name component) runs
    /// in its cores, after those checks. Before the base, the engine checked the whole name, then
    /// the token, then disposal, and the enumeration checked disposal at its first
    /// <c>MoveNextAsync</c>.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Engine: the base checks an empty name, then disposal, then the token, and the model's name rule after them")]
    public async Task Members_InvalidNameDisposedOrCanceled_ShouldCheckNameThenDisposalThenToken()
    {
        // Arrange
        var engine = DocumentDatabaseEngine.Create(new());
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var canceledOpen = await Should.ThrowAsync<OperationCanceledException>(async () => await engine.OpenDatabaseAsync("test", canceled.Token));
        var canceledComponent = await Should.ThrowAsync<OperationCanceledException>(async () => await engine.OpenDatabaseAsync("..", canceled.Token));
        var component = await Should.ThrowAsync<ArgumentException>(async () => await engine.OpenDatabaseAsync(".."));
        var unnamedLookup = Should.Throw<ArgumentException>(() => engine.TryGetDatabase(default, out _));
        await engine.DisposeAsync();

        // Act
        var unnamedCreate = await Should.ThrowAsync<ArgumentException>(async () => await engine.CreateDatabaseAsync(default, canceled.Token));
        var disposedCreate = await Should.ThrowAsync<ObjectDisposedException>(async () => await engine.CreateDatabaseAsync("test", canceled.Token));
        var disposedComponent = await Should.ThrowAsync<ObjectDisposedException>(async () => await engine.DropDatabaseAsync(".."));
        var disposedLookup = Should.Throw<ObjectDisposedException>(() => engine.TryGetDatabase("..", out _));
        var disposedEnumeration = Should.Throw<ObjectDisposedException>(() => engine.GetDatabasesAsync());

        // Assert
        canceledOpen.CancellationToken.ShouldBe(canceled.Token);
        canceledComponent.CancellationToken.ShouldBe(canceled.Token);
        component.Message.ShouldStartWith("A database name must be a single file-name component.", Case.Sensitive);
        unnamedLookup.Message.ShouldStartWith("A database name is required.", Case.Sensitive);
        unnamedCreate.ParamName.ShouldBe("name");
        disposedCreate.ShouldNotBeNull();
        disposedComponent.ShouldNotBeNull();
        disposedLookup.ShouldNotBeNull();
        disposedEnumeration.ShouldNotBeNull();
    }

    /// <summary>
    /// Databases that fail to close are one component of the engine's disposal aggregate
    /// (concrete-types plan §6.4): one failure is reported as itself, and two or more inside one
    /// nested aggregate, "One or more document databases failed to close.". The engine's aggregate
    /// is the root base's, "One or more components of engine '{name}' failed to close.". Before the
    /// root base, the engine's single aggregate, "One or more document engine components failed to
    /// close.", held each database's failure directly.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Engine: databases that fail to close are one component of the engine's aggregate, several of them nested")]
    public async Task DisposeAsync_DatabasesFailToClose_ShouldReportThemAsOneComponent()
    {
        // Arrange: quiet workers, and a database of each engine holding a durable write; every
        // journal flush of the closes fails.
        var single = await CreateWithWritesAsync("single", databases: 1);
        var several = await CreateWithWritesAsync("several", databases: 2);

        // Act
        AggregateException singleFailure;
        AggregateException severalFailure;
        using (FaultInjectingJournalStorageStrategy.FailJournalFlushes(100))
        {
            singleFailure = await Should.ThrowAsync<AggregateException>(async () => await single.DisposeAsync());
            severalFailure = await Should.ThrowAsync<AggregateException>(async () => await several.DisposeAsync());
        }

        // Assert
        singleFailure.Message.ShouldStartWith("One or more components of engine 'single' failed to close.", Case.Sensitive);
        singleFailure.InnerExceptions.ShouldHaveSingleItem().ShouldBeOfType<StorageOfflineException>();
        severalFailure.Message.ShouldStartWith("One or more components of engine 'several' failed to close.", Case.Sensitive);
        var databases = severalFailure.InnerExceptions.ShouldHaveSingleItem().ShouldBeOfType<AggregateException>();
        databases.Message.ShouldStartWith("One or more document databases failed to close.", Case.Sensitive);
        databases.InnerExceptions.Count.ShouldBe(2);
        databases.InnerExceptions.ShouldAllBe(failure => failure is StorageOfflineException);
        single.State.ShouldBe(EngineState.Disposed);
        several.State.ShouldBe(EngineState.Disposed);

        static async Task<DocumentDatabaseEngine> CreateWithWritesAsync(string name, int databases)
        {
            var engine = DocumentDatabaseEngine.Create(new DocumentDatabaseEngineOptions
            {
                EngineName = name,
                StorageStrategy = new FaultInjectingJournalStorageStrategy(durable: true),
                CheckpointInterval = TimeSpan.FromHours(1),
                PageWriteBackInterval = TimeSpan.FromHours(1),
                MaintenanceInterval = TimeSpan.FromHours(1),
            });

            for (int index = 0; index < databases; index++)
            {
                var database = await engine.CreateDatabaseAsync($"{name}-{index}");
                await using var session = await database.CreateSessionAsync();
                var collection = await session.CreateCollectionAsync("items");
                await collection.PutAsync(session, "item", Encoding.UTF8.GetBytes("{\"id\":\"item\"}"));
            }

            return engine;
        }
    }
}
