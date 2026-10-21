using System;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Blob.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Blob.Catalog.Tests;

public sealed partial class BlobCatalogTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Blob.Catalog] - Late writer: one invisible publication has constant lookup work (#1281)")]
    [InlineData(32, false)]
    [InlineData(32, true)]
    [InlineData(256, false)]
    [InlineData(256, true)]
    public async Task FindBlob_LateReadCommittedWriter_ShouldBoundPreSnapshotWork(int versions, bool reopen)
    {
        // Arrange
        await using var fixture = new CatalogHistoryDatabase();
        var parent = Guid.NewGuid();
        var entry = Entry(parent, "item");
        var earlier = await fixture.Coordinator.BeginAsync(IsolationLevel.ReadCommitted);
        for (int version = 1; version <= versions; version++)
        {
            var writer = await fixture.Coordinator.BeginAsync(IsolationLevel.Snapshot);
            ulong value = (ulong)version;
            await fixture.Catalog.SaveBlobAsync(entry with { ETag = value }, writer);
            await fixture.Coordinator.CommitAsync(writer);
        }
        {
            var writer = earlier;
            ulong value = (ulong)versions + 1;
            await fixture.Catalog.SaveBlobAsync(entry with { ETag = value }, writer);
        }
        await fixture.Coordinator.CommitAsync(earlier);
        var pending = await fixture.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        {
            var writer = pending;
            ulong value = (ulong)versions + 2;
            await fixture.Catalog.SaveBlobAsync(entry with { ETag = value }, writer);
        }
        var reader = await fixture.Coordinator.BeginAsync(IsolationLevel.Snapshot);

        if (reopen) { fixture.Catalog = BlobCatalog.Open(fixture.Storage, fixture.Coordinator); }

        // Act
        long before = fixture.Catalog.RecordReadCount;
        var found = fixture.Catalog.FindBlob(parent, "item", reader.Snapshot);
        long reads = fixture.Catalog.RecordReadCount - before;
        _output.WriteLine($"{versions} pre-snapshot overwrites: record reads={reads}");

        // Assert
        found!.Value.ETag.ShouldBe((ulong)versions + 1);
        reads.ShouldBe(3);
        await fixture.Coordinator.RollbackAsync(reader);
        await fixture.Coordinator.RollbackAsync(pending);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Blob.Catalog] - Reopen: retained snapshot sees v2 in an all-deleted history (#1281)")]
    public async Task Open_AllTombstonedHistory_ShouldRetainSnapshotVisibleTerminalVersion()
    {
        // Arrange
        await using var fixture = new CatalogHistoryDatabase();
        var parent = Guid.NewGuid();
        var entry = Entry(parent, "item");
        for (ulong value = 1; value <= 2; value++)
        {
            var writer = await fixture.Coordinator.BeginAsync(IsolationLevel.Snapshot);
            await fixture.Catalog.SaveBlobAsync(entry with { ETag = value }, writer);
            await fixture.Coordinator.CommitAsync(writer);
        }
        var reader = await fixture.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        var expected = fixture.Catalog.FindBlob(parent, "item", reader.Snapshot);
        var deleting = await fixture.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        await fixture.Catalog.DeleteBlobAsync(parent, "item", deleting);
        await fixture.Coordinator.CommitAsync(deleting);

        // Act
        var reopened = BlobCatalog.Open(fixture.Storage, fixture.Coordinator);

        // Assert
        expected!.Value.ETag.ShouldBe(2UL);
        reopened.FindBlob(parent, "item", reader.Snapshot).ShouldBe(expected);
        reopened.GetBlobs(parent, null, reader.Snapshot).ShouldHaveSingleItem().ShouldBe(expected.Value);
        fixture.Coordinator.RunVersionPurgePass(default);
        reopened.FindBlob(parent, "item", reader.Snapshot).ShouldBe(expected);
        await fixture.Coordinator.RollbackAsync(reader);
    }
}
