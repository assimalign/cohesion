using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Blob.Catalog;
using Assimalign.Cohesion.Database.Blob.Internal;
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
        var database = (IBlobDatabase)await engine.CreateDatabaseAsync("test");
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
    public async Task Transactions_bind_through_session_database_and_rollback_chunks_and_catalog()
    {
        await using var engine = BlobDatabaseEngine.Create(new());
        var database = (IBlobDatabase)await engine.CreateDatabaseAsync("test");
        var container = await database.CreateContainerAsync("files");
        await Write(container, "item", "old"u8.ToArray());
        await using var session = await database.CreateSessionAsync();
        var scoped = (IBlobDatabase)session.Database;
        var transactionalContainer = await scoped.GetContainerAsync("files");
        await using (var transaction = await session.BeginTransactionAsync())
        {
            await Write(transactionalContainer, "item", new byte[30_000]);
            (await transactionalContainer.GetPropertiesAsync("item"))!.Value.Length.ShouldBe(30_000);
            (await Read(container, "item")).ShouldBe("old"u8.ToArray());
            await scoped.CreateContainerAsync("temporary");
            await transaction.RollbackAsync();
        }
        (await Read(container, "item")).ShouldBe("old"u8.ToArray());
        await Should.ThrowAsync<DatabaseException>(async () => await database.GetContainerAsync("temporary"));
        await using (var transaction = await session.BeginTransactionAsync())
        {
            var stream = await transactionalContainer.OpenWriteAsync("item");
            await stream.WriteAsync("committed"u8.ToArray());
            await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync());
            await stream.DisposeAsync();
            await transaction.CommitAsync();
        }
        (await Read(container, "item")).ShouldBe("committed"u8.ToArray());
    }

    [Theory]
    [InlineData(IsolationLevel.Snapshot, "before")]
    [InlineData(IsolationLevel.ReadCommitted, "after")]
    public async Task Session_reads_observe_requested_isolation(IsolationLevel level, string expected)
    {
        await using var engine = BlobDatabaseEngine.Create(new());
        var database = (IBlobDatabase)await engine.CreateDatabaseAsync("test");
        var container = await database.CreateContainerAsync("files");
        await Write(container, "item", "before"u8.ToArray());
        await using var session = await database.CreateSessionAsync();
        await using var transaction = await session.BeginTransactionAsync(level);
        var scoped = await ((IBlobDatabase)session.Database).GetContainerAsync("files");
        await Write(container, "item", "after"u8.ToArray());
        Encoding.UTF8.GetString(await Read(scoped, "item")).ShouldBe(expected);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Stale_snapshot_writer_fails_without_losing_newer_version()
    {
        await using var engine = BlobDatabaseEngine.Create(new());
        var database = (IBlobDatabase)await engine.CreateDatabaseAsync("test");
        var container = await database.CreateContainerAsync("files");
        await Write(container, "item", "before"u8.ToArray());
        await using var session = await database.CreateSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        var scoped = await ((IBlobDatabase)session.Database).GetContainerAsync("files");
        await Write(container, "item", "after"u8.ToArray());
        await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () => await scoped.DeleteAsync("item"));
        transaction.State.ShouldBe(TransactionState.RolledBack);
        (await Read(container, "item")).ShouldBe("after"u8.ToArray());
    }

    [Fact]
    public async Task Cancelled_stream_is_never_published()
    {
        await using var engine = BlobDatabaseEngine.Create(new());
        var database = (IBlobDatabase)await engine.CreateDatabaseAsync("test");
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
        var database = (BlobDatabaseInstance)await engine.CreateDatabaseAsync("test");
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
        var database = (BlobDatabaseInstance)await engine.CreateDatabaseAsync("test");
        var context = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        await database.Catalog.SaveContainerAsync(new BlobContainerMetadata(Guid.NewGuid(), "managed", DatabaseObjectOwner.Schema, "MediaSchema"), context);
        await database.Coordinator.CommitAsync(context);
        var error = await Should.ThrowAsync<DatabaseObjectLockedException>(async () => await database.DropContainerAsync("managed"));
        error.Message.ShouldContain("managed");
        error.Message.ShouldContain("MediaSchema");
        error.Message.ShouldContain("DROP CONTAINER");
        await using var session = await database.CreateSessionAsync();
        await ((IBlobDatabase)session.Database).CreateContainerAsync("adhoc");
        var read = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        database.Catalog.FindContainer("adhoc", read.Snapshot)!.Value.Owner.ShouldBe(DatabaseObjectOwner.Adhoc);
        await database.Coordinator.RollbackAsync(read);
        await database.DropContainerAsync("adhoc");
    }

    [Fact]
    public async Task Container_drop_is_transactional_and_stale_handles_cannot_address_recreated_container()
    {
        await using var engine = BlobDatabaseEngine.Create(new());
        var database = (IBlobDatabase)await engine.CreateDatabaseAsync("test");
        var original = await database.CreateContainerAsync("files");
        await Write(original, "item", new byte[20_000]);
        await using var session = await database.CreateSessionAsync();
        await using var transaction = await session.BeginTransactionAsync();
        await ((IBlobDatabase)session.Database).DropContainerAsync("files");
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
            var database = (IBlobDatabase)await engine.CreateDatabaseAsync("Media");
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
            var loaded = (IBlobDatabase)await reopened.OpenDatabaseAsync("media");
            (await Read(await loaded.GetContainerAsync("files"), "item")).ShouldBe("durable"u8.ToArray());
            await reopened.DropDatabaseAsync("MEDIA");
            reopened.TryGetDatabase("media", out _).ShouldBeFalse();
            await Should.ThrowAsync<DatabaseNotFoundException>(async () => await reopened.OpenDatabaseAsync("Media"));
            await Should.ThrowAsync<ArgumentException>(async () => await reopened.CreateDatabaseAsync("../escape"));
        }
        finally { if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); } }
    }

    internal static async Task Write(IBlobContainer container, string name, byte[] bytes)
    {
        await using var stream = await container.OpenWriteAsync(name);
        await stream.WriteAsync(bytes);
    }
    internal static async Task<byte[]> Read(IBlobContainer container, string name)
    {
        await using var stream = await container.OpenReadAsync(name);
        using var result = new MemoryStream();
        await stream.CopyToAsync(result);
        return result.ToArray();
    }
}

