using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Blob.Storage;
using Assimalign.Cohesion.Database.Storage.Units;
using Assimalign.Cohesion.Database.Transactions;

using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Blob.Catalog.Tests;

public sealed partial class BlobCatalogTests
{
    private readonly ITestOutputHelper _output;

    public BlobCatalogTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(DisplayName = "Cohesion Test [Database.Blob.Catalog] - Deletion: current absence has constant reads while old snapshots retain versions (#1281)")]
    public async Task FindBlob_DeletedRetainedHistory_ShouldBoundCurrentRecordReads()
    {
        // Arrange
        await using var database = new TestDatabase();
        var coordinator = database.Coordinator;
        var catalog = database.Catalog;
        var parent = Guid.NewGuid();
        var entry = Entry(parent, "item");
        var seed = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        await catalog.SaveBlobAsync(entry, seed);
        await coordinator.CommitAsync(seed);
        var pinned = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var original = catalog.FindBlob(parent, "item", pinned.Snapshot);
        for (int version = 0; version < 64; version++)
        {
            var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
            await catalog.SaveBlobAsync(entry with { ETag = (ulong)version + 100 }, writer);
            await coordinator.CommitAsync(writer);
        }
        var deleting = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        await catalog.DeleteBlobAsync(parent, "item", deleting);
        catalog.FindBlob(parent, "item", deleting.Snapshot).ShouldBeNull();
        await coordinator.RollbackAsync(deleting);
        var restored = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        catalog.FindBlob(parent, "item", restored.Snapshot).ShouldNotBeNull();
        await coordinator.RollbackAsync(restored);
        var delete = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        await catalog.DeleteBlobAsync(parent, "item", delete);
        await coordinator.CommitAsync(delete);
        var current = await coordinator.BeginAsync(IsolationLevel.Snapshot);

        // Act
        for (int round = 0; round < 3; round++)
        {
            long before = catalog.RecordReadCount;
            catalog.FindBlob(parent, "item", current.Snapshot).ShouldBeNull();
            (catalog.RecordReadCount - before).ShouldBe(3);
        }

        // Assert
        catalog.FindBlob(parent, "item", pinned.Snapshot).ShouldBe(original);
        catalog.GetBlobs(parent, null, current.Snapshot).ShouldBeEmpty();
        await coordinator.RollbackAsync(current);
        await coordinator.RollbackAsync(pinned);
        coordinator.RunVersionPurgePass(default).ShouldBeGreaterThan(0);
        var fresh = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        long beforePurgeLookup = catalog.RecordReadCount;
        catalog.FindBlob(parent, "item", fresh.Snapshot).ShouldBeNull();
        (catalog.RecordReadCount - beforePurgeLookup).ShouldBeLessThanOrEqualTo(3);
        catalog.CachedReferenceCount.ShouldBe(0);
        await coordinator.RollbackAsync(fresh);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Blob.Catalog] - Rollback: many own versions restore the prior head with constant reads (#1281)")]
    [InlineData(32)]
    [InlineData(256)]
    public async Task FindBlob_RolledBackOwnHistory_ShouldBoundCurrentRecordReads(int versions)
    {
        // Arrange
        await using var database = new TestDatabase();
        var coordinator = database.Coordinator;
        var catalog = database.Catalog;
        var parent = Guid.NewGuid();
        var entry = Entry(parent, "item");
        var seed = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        await catalog.SaveBlobAsync(entry, seed);
        await coordinator.CommitAsync(seed);
        var pinned = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var original = catalog.FindBlob(parent, "item", pinned.Snapshot);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        for (int version = 0; version < versions; version++)
        {
            await catalog.SaveBlobAsync(entry with { ETag = (ulong)version + 100 }, writer);
        }
        await coordinator.RollbackAsync(writer);
        var reader = await coordinator.BeginAsync(IsolationLevel.Snapshot);

        // Act
        long reads = long.MaxValue;
        for (int round = 0; round < 3; round++)
        {
            long before = catalog.RecordReadCount;
            catalog.FindBlob(parent, "item", reader.Snapshot).ShouldBe(original);
            (catalog.RecordReadCount - before).ShouldBeLessThanOrEqualTo(3);
            reads = Math.Min(reads, catalog.RecordReadCount - before);
        }

        // Assert
        reads.ShouldBe(2);
        catalog.FindBlob(parent, "item", pinned.Snapshot).ShouldBe(original);
        await coordinator.RollbackAsync(reader);
        await coordinator.RollbackAsync(pinned);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Blob.Catalog] - Lookup: writer order survives late publication, reopen and same-writer rollback (#1281)")]
    public async Task FindBlob_OutOfOrderPublication_ShouldPreserveVersionSelection()
    {
        // Arrange: a read-committed writer begins first but replaces a later writer.
        await using var database = new TestDatabase();
        var container = new BlobContainerMetadata(Guid.NewGuid(), "files");
        var entry = Entry(container.Id, "item");
        var earlier = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted);
        var later = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        await database.Catalog.SaveBlobAsync(entry with { ETag = 2 }, later);
        await database.Coordinator.CommitAsync(later);
        var oldReader = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        await database.Catalog.SaveBlobAsync(entry with { ETag = 1 }, earlier);
        await database.Coordinator.CommitAsync(earlier);
        var reader = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);

        // Act
        var reopened = BlobCatalog.Open(database.Storage, database.Coordinator);
        var selected = reopened.FindBlob(container.Id, "item", reader.Snapshot);
        reopened.FindBlob(container.Id, "item", oldReader.Snapshot).ShouldBe(entry with { ETag = 2 });
        reopened.FindBlob(container.Id, "item", reader.Snapshot).ShouldBe(selected);
        database.Catalog.FindBlob(container.Id, "item", oldReader.Snapshot).ShouldBe(entry with { ETag = 2 });
        database.Catalog.FindBlob(container.Id, "item", reader.Snapshot).ShouldBe(selected);
        var writer = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        for (int version = 3; version <= 23; version++)
        {
            await database.Catalog.SaveBlobAsync(entry with { ETag = (ulong)version }, writer);
        }
        var own = database.Catalog.FindBlob(container.Id, "item", writer.Snapshot);
        await database.Coordinator.RollbackAsync(writer);

        // Assert
        // The first lookup after rollback uses the older snapshot. Its visible tombstone
        // must not become the current head before the newer snapshot reads again.
        database.Catalog.FindBlob(container.Id, "item", oldReader.Snapshot).ShouldBe(entry with { ETag = 2 });
        selected.ShouldBe(entry with { ETag = 1 });
        own.ShouldBe(entry with { ETag = 23 });
        database.Catalog.FindBlob(container.Id, "item", reader.Snapshot).ShouldBe(selected);
        await database.Coordinator.RollbackAsync(reader);
        await database.Coordinator.RollbackAsync(oldReader);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Blob.Catalog] - Overwrites: explicit purge bounds the data file, directory and current lookup work (#1281)")]
    public async Task SaveBlob_SustainedContentOverwrites_ShouldReusePagesAndBoundReads()
    {
        // Arrange: 16 KiB per overwrite, 32 overwrites per explicit purge, no workers.
        await using var database = new TestDatabase();
        var containerId = Guid.NewGuid();
        byte[] payload = new byte[16 * 1024];
        BlobCatalogEntry? previous = null;
        long plateau = 0;
        for (int batch = 0; batch < 12; batch++)
        {
            for (int version = 0; version < 32; version++)
            {
                var writer = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
                BlobContentReference content = default;
                await using (var upload = database.Storage.OpenWrite(database.Coordinator, writer,
                    value => { content = value; return default; }, _ => default, CancellationToken.None))
                {
                    payload[0] = (byte)(batch + version);
                    await upload.WriteAsync(payload, CancellationToken.None);
                }
                if (previous is { } old)
                {
                    await database.Storage.TombstoneContentAsync(database.Coordinator, writer,
                        new BlobContentReference(old.HeadLocation, old.Length, old.Checksum), CancellationToken.None);
                }
                var entry = Entry(containerId, "item") with
                {
                    ETag = (ulong)(batch * 32 + version + 1),
                    HeadLocation = content.Head,
                    Length = content.Length,
                    Checksum = content.Checksum
                };

                // Act
                await database.Catalog.SaveBlobAsync(entry, writer, CancellationToken.None);
                await database.Coordinator.CommitAsync(writer);
                previous = entry;
            }
            database.Coordinator.RunVersionPurgePass(default).ShouldBeGreaterThan(0);
            database.Coordinator.Checkpoint();
            if (batch == 2) { plateau = database.Data.Length; }
            _output.WriteLine($"Batch {batch + 1}: data bytes={database.Data.Length}, cached references={database.Catalog.CachedReferenceCount}");

            // Assert: allow one batch of pages for rotating checkpoint anchors and partially filled pages.
            if (batch > 2) { database.Data.Length.ShouldBeLessThanOrEqualTo(plateau + 128 * Page.Size); }
            var reader = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
            for (int round = 0; round < 3; round++)
            {
                long before = database.Catalog.RecordReadCount;
                database.Catalog.FindBlob(containerId, "item", reader.Snapshot).ShouldBe(previous);
                (database.Catalog.RecordReadCount - before).ShouldBeLessThanOrEqualTo(3);
            }
            await using (var download = database.Storage.OpenRead(new BlobContentReference(previous!.Value.HeadLocation,
                previous.Value.Length, previous.Value.Checksum)))
            {
                using var bytes = new MemoryStream();
                await download.CopyToAsync(bytes, CancellationToken.None);
                bytes.ToArray().ShouldBe(payload);
            }
            await database.Coordinator.RollbackAsync(reader);
            database.Catalog.CachedReferenceCount.ShouldBeLessThanOrEqualTo(33);
            database.Storage.GetOwnerPages(0).Count.ShouldBeLessThanOrEqualTo(2);
            database.Storage.GetOwnerPages(BlobStorage.ContentOwner).Count.ShouldBeLessThanOrEqualTo(3);
        }
        plateau.ShouldBeGreaterThan(0);
        var fresh = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        for (int probe = 0; probe < 32; probe++) { database.Catalog.FindBlob(containerId, "item", fresh.Snapshot); }
        database.Catalog.CachedReferenceCount.ShouldBe(1);
        await database.Coordinator.RollbackAsync(fresh);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Blob.Catalog] - Save: cancellation publishes no reference and later reads still progress (#1281)")]
    public async Task SaveBlob_CanceledApply_ShouldKeepDirectoryUsable()
    {
        // Arrange
        await using var database = new TestDatabase();
        var writer = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        // Act
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await database.Catalog.SaveBlobAsync(Entry(Guid.NewGuid(), "item"), writer, canceled.Token));
        await database.Coordinator.RollbackAsync(writer);
        var reader = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);

        // Assert: use another thread to verify the directory monitor was released.
        database.Catalog.CachedReferenceCount.ShouldBe(0);
        (await DedicatedTestTask.Run(() => database.Catalog.GetContainers(reader.Snapshot))
            .WaitAsync(TimeSpan.FromSeconds(10))).ShouldBeEmpty();
        await database.Coordinator.RollbackAsync(reader);
    }
}
