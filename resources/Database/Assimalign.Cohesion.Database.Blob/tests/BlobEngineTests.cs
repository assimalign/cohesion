using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Blob.Catalog;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Blob.Tests;

public sealed class BlobEngineTests
{
    [Fact]
    public async Task Streaming_publish_is_atomic_and_metadata_is_preserved_across_replacement()
    {
        await using var engine = BlobDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("test");
        var container = await database.CreateContainerAsync("files");
        byte[] original = Encoding.UTF8.GetBytes("original");
        await Write(container, "a/file", original);
        var before = (await container.GetPropertiesAsync("a/file"))!.Value;
        await using var oldReader = await container.OpenReadAsync("a/file");
        var payload = new byte[110_321];
        for (int i = 0; i < payload.Length; i++) { payload[i] = (byte)(i * 31); }
        var upload = await container.OpenWriteAsync("a/file", new() { ContentType = "application/test" });
        await upload.WriteAsync(payload.AsMemory(0, 40_000));
        (await Read(container, "a/file")).ShouldBe(original);
        (await container.GetPropertiesAsync("a/file"))!.Value.ETag.ShouldBe(before.ETag);
        await upload.WriteAsync(payload.AsMemory(40_000));
        await upload.DisposeAsync();
        var after = (await container.GetPropertiesAsync("a/file"))!.Value;
        after.Length.ShouldBe(payload.Length);
        after.ContentType.ShouldBe("application/test");
        after.CreatedAt.ShouldBe(before.CreatedAt);
        after.ModifiedAt.ShouldBeGreaterThanOrEqualTo(before.ModifiedAt);
        after.ETag.ShouldNotBe(before.ETag);
        (await Read(container, "a/file")).ShouldBe(payload);
        using var oldContent = new MemoryStream();
        await oldReader.CopyToAsync(oldContent);
        oldContent.ToArray().ShouldBe(original);
        await Should.ThrowAsync<DatabaseException>(async () => await container.OpenWriteAsync("a/file", new() { Overwrite = false }));
        await Write(container, "b/empty", []);
        var names = new List<string>();
        await foreach (var blob in container.GetBlobsAsync("a/")) { names.Add(blob.Name); }
        names.ShouldBe(["a/file"]);
        (await Read(container, "b/empty")).ShouldBeEmpty();
        (await container.GetPropertiesAsync("b/empty"))!.Value.Checksum.ShouldBe(0u);
    }

    [Fact]
    public async Task Transactions_bind_through_the_session_and_rollback_chunks_and_catalog()
    {
        await using var engine = BlobDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("test");
        var container = await database.CreateContainerAsync("files");
        await Write(container, "item", "old"u8.ToArray());
        await using var session = await database.CreateSessionAsync();
        var transactionalContainer = await session.GetContainerAsync("files");
        await using (var transaction = await session.BeginTransactionAsync())
        {
            await Write(transactionalContainer, "item", new byte[30_000]);
            (await transactionalContainer.GetPropertiesAsync("item"))!.Value.Length.ShouldBe(30_000);
            (await Read(container, "item")).ShouldBe("old"u8.ToArray());
            await session.CreateContainerAsync("temporary");
            await transaction.RollbackAsync();
        }
        (await Read(container, "item")).ShouldBe("old"u8.ToArray());
        await Should.ThrowAsync<DatabaseException>(async () => await database.GetContainerAsync("temporary"));
        await using (var transaction = await session.BeginTransactionAsync())
        {
            var stream = await transactionalContainer.OpenWriteAsync("item");
            await stream.WriteAsync("committed"u8.ToArray());

            // An open stream is an operation of the transaction: the root base refuses the commit
            // with its message (concrete-types plan §6.4), for the model's former "Dispose every
            // blob stream before committing its transaction.", and leaves the transaction active.
            var refusal = await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync());
            refusal.Message.ShouldBe("An operation of the transaction is still running; commit after it completes.");
            transaction.State.ShouldBe(TransactionState.Active);
            await stream.DisposeAsync();
            await transaction.CommitAsync();
        }
        (await Read(container, "item")).ShouldBe("committed"u8.ToArray());
    }

    /// <summary>
    /// The token is observed only before the rollback starts (#1226), so nothing of a rollback
    /// canceled by then ran, and no purge pass may undo the still-active writer.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Rollback: a token canceled before the rollback starts leaves the transaction and its writes intact")]
    public async Task RollbackAsync_TokenCanceledBeforeStart_ShouldLeaveTransactionAndItsWritesIntact()
    {
        // Arrange
        await using var engine = BlobDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("test");
        await database.CreateContainerAsync("files");
        await using var session = await database.CreateSessionAsync();
        var scoped = await session.GetContainerAsync("files");
        var transaction = await session.BeginTransactionAsync();
        await Write(scoped, "item", "kept"u8.ToArray());
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        // Act
        await Should.ThrowAsync<OperationCanceledException>(async () => await transaction.RollbackAsync(canceled.Token));
        var stateAfterRollback = transaction.State;
        int pendingAfterRollback = database.Coordinator.VersionStore.PendingAbortedPurges.Count;
        database.Coordinator.RunVersionPurgePass(CancellationToken.None);
        await transaction.CommitAsync();

        // Assert
        stateAfterRollback.ShouldBe(TransactionState.Active);
        pendingAfterRollback.ShouldBe(0);
        transaction.State.ShouldBe(TransactionState.Committed);
        (await Read(await database.GetContainerAsync("files"), "item")).ShouldBe("kept"u8.ToArray());
    }

    [Theory]
    [InlineData(IsolationLevel.Snapshot, "before")]
    [InlineData(IsolationLevel.ReadCommitted, "after")]
    public async Task Session_reads_observe_requested_isolation(IsolationLevel level, string expected)
    {
        await using var engine = BlobDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("test");
        var container = await database.CreateContainerAsync("files");
        await Write(container, "item", "before"u8.ToArray());
        await using var session = await database.CreateSessionAsync();
        await using var transaction = await session.BeginTransactionAsync(level);
        var scoped = await session.GetContainerAsync("files");
        await Write(container, "item", "after"u8.ToArray());
        Encoding.UTF8.GetString(await Read(scoped, "item")).ShouldBe(expected);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Stale_snapshot_writer_fails_without_losing_newer_version()
    {
        await using var engine = BlobDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("test");
        var container = await database.CreateContainerAsync("files");
        await Write(container, "item", "before"u8.ToArray());
        await using var session = await database.CreateSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        var scoped = await session.GetContainerAsync("files");
        await Write(container, "item", "after"u8.ToArray());
        await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () => await scoped.DeleteAsync("item"));
        // The conflict aborts the explicit transaction, which waits for the caller's rollback (#1225).
        transaction.State.ShouldBe(TransactionState.Faulted);
        await transaction.RollbackAsync();
        transaction.State.ShouldBe(TransactionState.RolledBack);
        (await Read(container, "item")).ShouldBe("after"u8.ToArray());
    }

    [Fact]
    public async Task Cancelled_stream_is_never_published()
    {
        await using var engine = BlobDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("test");
        var container = await database.CreateContainerAsync("files");
        using var cancellation = new CancellationTokenSource();
        var stream = await container.OpenWriteAsync("partial", cancellationToken: cancellation.Token);
        await stream.WriteAsync(new byte[20_000]);
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () => await stream.WriteAsync(new byte[100]));
        await stream.DisposeAsync();
        (await container.GetPropertiesAsync("partial")).ShouldBeNull();
    }

    [Fact]
    public async Task Deleted_blob_releases_pages_after_read_snapshot_closes()
    {
        await using var engine = BlobDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("test");
        var container = await database.CreateContainerAsync("files");
        await Write(container, "large", new byte[100_000]);
        var reader = await container.OpenReadAsync("large");
        (await container.DeleteAsync("large")).ShouldBeTrue();
        (await container.DeleteAsync("large")).ShouldBeFalse();
        (await container.GetPropertiesAsync("large")).ShouldBeNull();
        database.Coordinator.RunVersionPurgePass(CancellationToken.None).ShouldBe(0);
        using var oldBytes = new MemoryStream();
        await reader.CopyToAsync(oldBytes);
        oldBytes.Length.ShouldBe(100_000);
        await reader.DisposeAsync();
        database.Coordinator.RunVersionPurgePass(CancellationToken.None).ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Schema_owned_container_drop_has_sql_ownership_semantics()
    {
        await using var engine = BlobDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("test");
        var context = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        await database.Catalog.SaveContainerAsync(new BlobContainerMetadata(Guid.NewGuid(), "managed", DatabaseObjectOwner.Schema, "MediaSchema"), context);
        await database.Coordinator.CommitAsync(context);
        var error = await Should.ThrowAsync<DatabaseObjectLockedException>(async () => await database.DropContainerAsync("managed"));
        error.Message.ShouldContain("managed");
        error.Message.ShouldContain("MediaSchema");
        error.Message.ShouldContain("DROP CONTAINER");
        await using var session = await database.CreateSessionAsync();
        await session.CreateContainerAsync("adhoc");
        var read = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        database.Catalog.FindContainer("adhoc", read.Snapshot)!.Value.Owner.ShouldBe(DatabaseObjectOwner.Adhoc);
        await database.Coordinator.RollbackAsync(read);
        await database.DropContainerAsync("adhoc");
    }

    [Fact]
    public async Task Container_drop_is_transactional_and_stale_handles_cannot_address_recreated_container()
    {
        await using var engine = BlobDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("test");
        var original = await database.CreateContainerAsync("files");
        await Write(original, "item", new byte[20_000]);
        await using var session = await database.CreateSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        await session.DropContainerAsync("files");
        (await original.GetPropertiesAsync("item")).ShouldNotBeNull();
        await transaction.RollbackAsync();
        (await Read(original, "item")).Length.ShouldBe(20_000);
        await database.DropContainerAsync("files");
        await database.CreateContainerAsync("files");
        await Should.ThrowAsync<DatabaseException>(async () => await original.GetPropertiesAsync("item"));
    }

    [Fact]
    public async Task File_lifecycle_reopens_enumerates_drops_and_disposes_idempotently()
    {
        string path = Path.Combine(Path.GetTempPath(), "cohesion-blob-" + Guid.NewGuid().ToString("N"));
        try
        {
            var engine = BlobDatabaseEngine.Create(new() { RootPath = path });
            engine.Workers.Select(worker => worker.Kind).ShouldBe([
                DatabaseEngineWorkerKind.WriteAheadFlush, DatabaseEngineWorkerKind.PageWriteBack,
                DatabaseEngineWorkerKind.Checkpoint, DatabaseEngineWorkerKind.VersionPurge]);
            var database = await engine.CreateDatabaseAsync("Media");
            engine.TryGetDatabase("media", out var found).ShouldBeTrue();
            found.ShouldBeSameAs(database);
            var container = await database.CreateContainerAsync("files");
            await Write(container, "item", "durable"u8.ToArray());
            await engine.DisposeAsync();
            engine.Dispose();
            engine.State.ShouldBe(EngineState.Disposed);
            await Should.ThrowAsync<ObjectDisposedException>(async () => await container.OpenReadAsync("item"));
            await using var reopened = BlobDatabaseEngine.Create(new() { RootPath = path });
            var names = new List<string>();
            await foreach (var item in reopened.GetDatabasesAsync()) { names.Add(item.Name.ToString()); }
            names.ShouldBe(["Media"]);
            var loaded = await reopened.OpenDatabaseAsync("media");
            (await Read(await loaded.GetContainerAsync("files"), "item")).ShouldBe("durable"u8.ToArray());
            await reopened.DropDatabaseAsync("MEDIA");
            reopened.TryGetDatabase("media", out _).ShouldBeFalse();
            await Should.ThrowAsync<DatabaseNotFoundException>(async () => await reopened.OpenDatabaseAsync("Media"));
            await Should.ThrowAsync<ArgumentException>(async () => await reopened.CreateDatabaseAsync("../escape"));
        }
        finally { if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); } }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Format: a database in storage format 2 (the format before #1253) is refused at open with COHDBS001 naming it, its files untouched (#1251, #1253)")]
    public async Task Open_StorageFormatTwo_ShouldBeRefusedNamingTheDatabase()
    {
        string root = Path.Combine(Path.GetTempPath(), "cohesion-blob-storage-format-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Arrange: a closed database whose page 0 names storage format 2, the format before #1253.
            await using (var engine = BlobDatabaseEngine.Create(new() { RootPath = root }))
            {
                var database = await engine.CreateDatabaseAsync("legacy");
                var container = await database.CreateContainerAsync("files");
                await Write(container, "item", "durable"u8.ToArray());
            }

            StorageFormatFiles.WriteVersion(Path.Combine(root, "legacy", "blob.dat"), version: 2);
            var before = Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);

            // Act
            await using var reopened = BlobDatabaseEngine.Create(new() { RootPath = root });
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
        finally { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
    }

    /// <summary>
    /// The engine's lookup is typed (concrete-types plan, §6.5): an <c>out var</c> call binds the
    /// typed overload, and an explicitly typed base <c>out</c> still binds the base's lookup.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Engine: the typed lookup binds an out-var call, and a base-typed out binds the base's")]
    public async Task TryGetDatabase_OutVarAndBaseTypedOut_ShouldBindTheTypedAndTheBaseLookups()
    {
        // Arrange
        await using var engine = BlobDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("test");

        // Act
        bool typedFound = engine.TryGetDatabase("test", out var typed);
        BlobDatabase? lookedUp = typed;
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
    /// when it is called. The blob engine's own name rule (a single file-name component) runs in
    /// its cores, after those checks. Before the base, the engine checked the whole name, then the
    /// token, then disposal, and the enumeration checked disposal at its first
    /// <c>MoveNextAsync</c>.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Engine: the base checks an empty name, then disposal, then the token, and the model's name rule after them")]
    public async Task Members_InvalidNameDisposedOrCanceled_ShouldCheckNameThenDisposalThenToken()
    {
        // Arrange
        var engine = BlobDatabaseEngine.Create(new());
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
    /// nested aggregate, "One or more blob databases failed to close.". The engine's aggregate is
    /// the root base's, "One or more components of engine '{name}' failed to close.". Before the
    /// root base, the engine's single aggregate, "One or more blob engine components failed to
    /// close.", held each database's failure directly.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Engine: databases that fail to close are one component of the engine's aggregate, several of them nested")]
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
        databases.Message.ShouldStartWith("One or more blob databases failed to close.", Case.Sensitive);
        databases.InnerExceptions.Count.ShouldBe(2);
        databases.InnerExceptions.ShouldAllBe(failure => failure is StorageOfflineException);
        single.State.ShouldBe(EngineState.Disposed);
        several.State.ShouldBe(EngineState.Disposed);

        static async Task<BlobDatabaseEngine> CreateWithWritesAsync(string name, int databases)
        {
            var engine = BlobDatabaseEngine.Create(new BlobDatabaseEngineOptions
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
                var container = await database.CreateContainerAsync("files");
                await Write(container, "item", "item"u8.ToArray());
            }

            return engine;
        }
    }

    internal static async Task Write(BlobContainer container, string name, byte[] bytes)
    {
        await using var stream = await container.OpenWriteAsync(name);
        await stream.WriteAsync(bytes);
    }
    internal static async Task<byte[]> Read(BlobContainer container, string name)
    {
        await using var stream = await container.OpenReadAsync(name);
        using var result = new MemoryStream();
        await stream.CopyToAsync(result);
        return result.ToArray();
    }
}

