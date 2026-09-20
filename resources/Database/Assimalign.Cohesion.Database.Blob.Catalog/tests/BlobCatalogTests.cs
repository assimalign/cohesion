using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Blob.Storage;
using Assimalign.Cohesion.Database.Transactions;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Blob.Catalog.Tests;

public sealed class BlobCatalogTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Blob.Catalog] - Listing: Should preserve metadata, ownership and ordinal prefix isolation")]
    public async Task Listing_ShouldPreserveMetadataAndContainerIsolation()
    {
        await using var database = new TestDatabase();
        var context = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        var first = new BlobContainerMetadata(Guid.NewGuid(), "archive", DatabaseObjectOwner.Schema, "ArchiveSchema");
        var second = new BlobContainerMetadata(Guid.NewGuid(), "live");
        await database.Catalog.SaveContainerAsync(first, context);
        await database.Catalog.SaveContainerAsync(second, context);
        var entry = Entry(first.Id, "reports/alpha");
        await database.Catalog.SaveBlobAsync(entry, context);
        await database.Catalog.SaveBlobAsync(Entry(first.Id, "Reports/beta"), context);
        await database.Catalog.SaveBlobAsync(Entry(second.Id, "reports/other"), context);
        await database.Coordinator.CommitAsync(context);

        var reader = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        database.Catalog.GetContainers(reader.Snapshot).Select(item => item.Name).ShouldBe(new[] { "archive", "live" });
        database.Catalog.FindContainer("archive", reader.Snapshot).ShouldBe(first);
        database.Catalog.GetBlobs(first.Id, "reports/", reader.Snapshot).ShouldHaveSingleItem().ShouldBe(entry);
        database.Catalog.GetBlobs(first.Id, null, reader.Snapshot).Count.ShouldBe(2);
        database.Catalog.FindBlob(second.Id, entry.Name, reader.Snapshot).ShouldBeNull();
        await database.Coordinator.RollbackAsync(reader);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Blob.Catalog] - MVCC: Should hide uncommitted metadata and restore it after rollback")]
    public async Task MetadataVersions_ShouldRespectSnapshotsAndRollback()
    {
        await using var database = new TestDatabase();
        var container = new BlobContainerMetadata(Guid.NewGuid(), "files");
        var original = Entry(container.Id, "item");
        var create = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        await database.Catalog.SaveContainerAsync(container, create);
        await database.Catalog.SaveBlobAsync(original, create);
        await database.Coordinator.CommitAsync(create);

        var oldReader = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        var writer = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        var replacement = original with { ETag = original.ETag + 1, ContentType = "application/new" };
        await database.Catalog.SaveBlobAsync(replacement, writer);
        await database.Catalog.DeleteContainerAsync(container.Id, writer);
        database.Catalog.FindBlob(container.Id, "item", writer.Snapshot).ShouldBe(replacement);
        database.Catalog.FindContainer("files", writer.Snapshot).ShouldBeNull();
        database.Catalog.FindBlob(container.Id, "item", oldReader.Snapshot).ShouldBe(original);
        database.Catalog.FindContainer("files", oldReader.Snapshot).ShouldBe(container);
        await database.Coordinator.RollbackAsync(writer);

        var reader = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        database.Catalog.FindBlob(container.Id, "item", reader.Snapshot).ShouldBe(original);
        database.Catalog.FindContainer("files", reader.Snapshot).ShouldBe(container);
        await database.Coordinator.RollbackAsync(reader);

        var committedWriter = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        await database.Catalog.SaveBlobAsync(replacement, committedWriter);
        await database.Coordinator.CommitAsync(committedWriter);
        database.Catalog.FindBlob(container.Id, "item", oldReader.Snapshot).ShouldBe(original);
        await database.Coordinator.RollbackAsync(oldReader);
        database.Coordinator.RunVersionPurgePass(default).ShouldBeGreaterThan(0);

        var fresh = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        database.Catalog.GetBlobs(container.Id, null, fresh.Snapshot).ShouldHaveSingleItem().ShouldBe(replacement);
        await database.Coordinator.RollbackAsync(fresh);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Blob.Catalog] - Crash: Should recover committed metadata and discard uncommitted publication")]
    public async Task Crash_ShouldPreserveCommittedMetadataAndScrubUncommittedVersions()
    {
        await using var database = new TestDatabase();
        var container = new BlobContainerMetadata(Guid.NewGuid(), "schema-files", DatabaseObjectOwner.Schema, "Documents");
        var original = Entry(container.Id, "retained");
        var create = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        await database.Catalog.SaveContainerAsync(container, create);
        await database.Catalog.SaveBlobAsync(original, create);
        await database.Coordinator.CommitAsync(create);

        var unfinished = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        await database.Catalog.SaveBlobAsync(original with { ETag = 12345 }, unfinished);
        await database.Catalog.SaveBlobAsync(Entry(container.Id, "unfinished"), unfinished);
        database.Storage.FlushPendingCommits();
        database.Storage.WriteBackDirtyPages(int.MaxValue);

        // Copy live persisted file assets before any disposal or logical rollback:
        // the new storage sees the same state as a separate process after a crash.
        using var reopenedStorage = BlobStorage.Open(
            Copy(database.Data),
            Copy(database.Journal),
            new MemoryStream(), checkpointOnOpen: false);
        await using var recovered = new TransactionCoordinator(reopenedStorage, reopenedStorage.WriteAheadJournal, reopenedStorage.Records);
        recovered.AnalyzeAndScrub();
        var catalog = BlobCatalog.Open(reopenedStorage, recovered);
        recovered.CompleteRecovery();
        var reader = await recovered.BeginAsync(IsolationLevel.Snapshot);
        catalog.FindContainer(container.Name, reader.Snapshot).ShouldBe(container);
        catalog.GetBlobs(container.Id, null, reader.Snapshot).ShouldHaveSingleItem().ShouldBe(original);
        catalog.FindBlob(container.Id, "unfinished", reader.Snapshot).ShouldBeNull();
        await recovered.RollbackAsync(reader);
        await database.Coordinator.RollbackAsync(unfinished);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Blob.Catalog] - Open: Should reject unsupported metadata formats")]
    public async Task Open_ShouldRejectUnsupportedFormat()
    {
        await using var database = new TestDatabase();
        byte[] malformed = new byte[34];
        malformed[16] = 1;
        malformed[17] = 255;
        using (var bracket = database.Storage.BeginTransaction())
        {
            database.Storage.InsertEntry(bracket, malformed);
            bracket.Commit();
        }
        Should.Throw<BlobCatalogException>(() => BlobCatalog.Open(database.Storage, database.Coordinator));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Blob.Catalog] - Read: Should surface malformed live metadata rather than report it absent")]
    public async Task Read_ShouldRejectMalformedLiveMetadata()
    {
        await using var database = new TestDatabase();
        var writer = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        await database.Catalog.SaveContainerAsync(new BlobContainerMetadata(Guid.NewGuid(), "files"), writer);
        await database.Coordinator.CommitAsync(writer);
        using (var iterator = database.Storage.GetUnitIterator(0))
        {
            iterator.MoveNext().ShouldBeTrue();
            var unit = iterator.Current;
            byte[] malformed = unit.Data.ToArray();
            malformed[16] = 99;
            using var bracket = database.Storage.BeginTransaction();
            database.Storage.UpdateEntry(bracket, unit.PageId, unit.SlotIndex, malformed);
            bracket.Commit();
        }
        var reader = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        Should.Throw<BlobCatalogException>(() => database.Catalog.FindContainer("files", reader.Snapshot));
        await database.Coordinator.RollbackAsync(reader);
    }

    private static BlobCatalogEntry Entry(Guid containerId, string name)
        => new(containerId, name, 0, "text/plain", 42,
            new DateTimeOffset(2026, 9, 17, 8, 30, 0, TimeSpan.FromHours(-4)),
            new DateTimeOffset(2026, 9, 17, 10, 30, 0, TimeSpan.FromHours(-4)), 0, 0);

    private static MemoryStream Copy(MemoryStream source)
    {
        var copy = new MemoryStream();
        copy.Write(source.ToArray());
        copy.Position = 0;
        return copy;
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        internal MemoryStream Data { get; } = new();
        internal MemoryStream Journal { get; } = new();
        internal BlobStorage Storage { get; }
        internal TransactionCoordinator Coordinator { get; }
        internal IBlobCatalog Catalog { get; }

        internal TestDatabase()
        {
            Storage = BlobStorage.Create(Data, Journal, new MemoryStream(), "catalog-tests");
            Coordinator = new TransactionCoordinator(Storage, Storage.WriteAheadJournal, Storage.Records);
            Catalog = BlobCatalog.Open(Storage, Coordinator);
        }

        public async ValueTask DisposeAsync()
        {
            await Coordinator.DisposeAsync();
            await Storage.DisposeAsync();
        }
    }
}
