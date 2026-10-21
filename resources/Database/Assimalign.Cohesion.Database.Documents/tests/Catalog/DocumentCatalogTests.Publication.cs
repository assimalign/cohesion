using System;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents.Catalog.Tests;

public sealed partial class DocumentCatalogTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Documents.Catalog] - Late writer: one invisible publication has constant lookup work (#1281)")]
    [InlineData(32, false)]
    [InlineData(32, true)]
    [InlineData(256, false)]
    [InlineData(256, true)]
    public async Task FindDocument_LateReadCommittedWriter_ShouldBoundPreSnapshotWork(int versions, bool reopen)
    {
        // Arrange
        await using var fixture = new CatalogHistoryDatabase();
        var parent = Guid.NewGuid();
        var earlier = await fixture.Coordinator.BeginAsync(IsolationLevel.ReadCommitted);
        for (int version = 1; version <= versions; version++)
        {
            var writer = await fixture.Coordinator.BeginAsync(IsolationLevel.Snapshot);
            ulong value = (ulong)version;
            await fixture.Catalog.SaveDocumentAsync(new DocumentCatalogEntry(parent, "item", value, 1, 2, 0), writer);
            await fixture.Coordinator.CommitAsync(writer);
        }
        {
            var writer = earlier;
            ulong value = (ulong)versions + 1;
            await fixture.Catalog.SaveDocumentAsync(new DocumentCatalogEntry(parent, "item", value, 1, 2, 0), writer);
        }
        await fixture.Coordinator.CommitAsync(earlier);
        var pending = await fixture.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        {
            var writer = pending;
            ulong value = (ulong)versions + 2;
            await fixture.Catalog.SaveDocumentAsync(new DocumentCatalogEntry(parent, "item", value, 1, 2, 0), writer);
        }
        var reader = await fixture.Coordinator.BeginAsync(IsolationLevel.Snapshot);

        if (reopen) { fixture.Catalog = DocumentCatalog.Open(fixture.Storage, fixture.Coordinator); }

        // Act
        long before = fixture.Catalog.RecordReadCount;
        var found = fixture.Catalog.FindDocument(parent, "item", reader.Snapshot);
        long reads = fixture.Catalog.RecordReadCount - before;
        _output.WriteLine($"{versions} pre-snapshot overwrites: record reads={reads}");

        // Assert
        found!.Value.Version.ShouldBe((ulong)versions + 1);
        reads.ShouldBe(3);
        await fixture.Coordinator.RollbackAsync(reader);
        await fixture.Coordinator.RollbackAsync(pending);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Documents.Catalog] - Reopen: retained snapshot sees v2 in an all-deleted history (#1281)")]
    public async Task Open_AllTombstonedHistory_ShouldRetainSnapshotVisibleTerminalVersion()
    {
        // Arrange
        await using var fixture = new CatalogHistoryDatabase();
        var parent = Guid.NewGuid();
        for (ulong value = 1; value <= 2; value++)
        {
            var writer = await fixture.Coordinator.BeginAsync(IsolationLevel.Snapshot);
            await fixture.Catalog.SaveDocumentAsync(new DocumentCatalogEntry(parent, "item", value, 1, 2, 0), writer);
            await fixture.Coordinator.CommitAsync(writer);
        }
        var reader = await fixture.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        var expected = fixture.Catalog.FindDocument(parent, "item", reader.Snapshot);
        var deleting = await fixture.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        await fixture.Catalog.DeleteDocumentAsync(parent, "item", deleting);
        await fixture.Coordinator.CommitAsync(deleting);

        // Act
        var reopened = DocumentCatalog.Open(fixture.Storage, fixture.Coordinator);

        // Assert
        expected!.Value.Version.ShouldBe(2UL);
        reopened.FindDocument(parent, "item", reader.Snapshot).ShouldBe(expected);
        reopened.GetDocuments(parent, null, reader.Snapshot).ShouldHaveSingleItem().ShouldBe(expected.Value);
        fixture.Coordinator.RunVersionPurgePass(default);
        reopened.FindDocument(parent, "item", reader.Snapshot).ShouldBe(expected);
        await fixture.Coordinator.RollbackAsync(reader);
    }
}
