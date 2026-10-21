using System;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents.Catalog.Tests;

public sealed partial class DocumentCatalogTests
{
    private readonly ITestOutputHelper _output;

    public DocumentCatalogTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory(DisplayName = "Cohesion Test [Database.Documents.Catalog] - Lookup: current reads have constant work while a snapshot retains history (#1281)")]
    [InlineData(32)]
    [InlineData(256)]
    public async Task FindDocument_RetainedHistory_ShouldBoundCurrentRecordReads(int versions)
    {
        // Arrange
        await using var fixture = await Fixture.Create();
        await fixture.Write("item", "{}");
        var pinned = await fixture.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        var original = fixture.Catalog.FindDocument(fixture.Collection.Id, "item", pinned.Snapshot);
        for (int version = 1; version <= versions; version++) { await fixture.Write("item", "{}"); }
        var reader = await fixture.Coordinator.BeginAsync(IsolationLevel.Snapshot);

        // Act
        long reads = long.MaxValue;
        for (int round = 0; round < 3; round++)
        {
            long before = fixture.Catalog.RecordReadCount;
            fixture.Catalog.FindDocument(fixture.Collection.Id, "item", reader.Snapshot)!.Value.Version.ShouldBe((ulong)versions + 1);
            reads = Math.Min(reads, fixture.Catalog.RecordReadCount - before);
        }

        // Assert: the bound includes both rotating cleanup probes.
        _output.WriteLine($"{versions} overwrites: minimum record reads of 3 rounds={reads}");
        reads.ShouldBe(3);
        fixture.Catalog.FindDocument(fixture.Collection.Id, "item", pinned.Snapshot).ShouldBe(original);
        fixture.Coordinator.RunVersionPurgePass(default).ShouldBe(0);
        await fixture.Coordinator.RollbackAsync(reader);
        await fixture.Coordinator.RollbackAsync(pinned);
        fixture.Coordinator.RunVersionPurgePass(default).ShouldBeGreaterThan(0);
        var fresh = await fixture.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        for (int probe = 0; probe <= versions; probe++)
        {
            long before = fixture.Catalog.RecordReadCount;
            fixture.Catalog.GetDocuments(fixture.Collection.Id, null, fresh.Snapshot).ShouldHaveSingleItem();
            (fixture.Catalog.RecordReadCount - before).ShouldBeLessThanOrEqualTo(3);
        }
        fixture.Catalog.CachedReferenceCount.ShouldBe(2);
        await fixture.Coordinator.RollbackAsync(fresh);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Documents.Catalog] - Deletion: current absence has constant reads while old snapshots retain versions (#1281)")]
    public async Task FindDocument_DeletedRetainedHistory_ShouldBoundCurrentRecordReads()
    {
        // Arrange
        await using var fixture = await Fixture.Create();
        var coordinator = fixture.Coordinator;
        var catalog = fixture.Catalog;
        var parent = fixture.Collection.Id;
        await fixture.Write("item", "{}");
        var pinned = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var original = catalog.FindDocument(parent, "item", pinned.Snapshot);
        for (int version = 0; version < 64; version++)
        {
            await fixture.Write("item", "{}");
        }
        var deleting = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        await catalog.DeleteDocumentAsync(parent, "item", deleting);
        catalog.FindDocument(parent, "item", deleting.Snapshot).ShouldBeNull();
        await coordinator.RollbackAsync(deleting);
        var restored = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        catalog.FindDocument(parent, "item", restored.Snapshot).ShouldNotBeNull();
        await coordinator.RollbackAsync(restored);
        var delete = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        await catalog.DeleteDocumentAsync(parent, "item", delete);
        await coordinator.CommitAsync(delete);
        var current = await coordinator.BeginAsync(IsolationLevel.Snapshot);

        // Act
        for (int round = 0; round < 3; round++)
        {
            long before = catalog.RecordReadCount;
            catalog.FindDocument(parent, "item", current.Snapshot).ShouldBeNull();
            (catalog.RecordReadCount - before).ShouldBe(3);
        }

        // Assert
        catalog.FindDocument(parent, "item", pinned.Snapshot).ShouldBe(original);
        catalog.GetDocuments(parent, null, current.Snapshot).ShouldBeEmpty();
        await coordinator.RollbackAsync(current);
        await coordinator.RollbackAsync(pinned);
        coordinator.RunVersionPurgePass(default).ShouldBeGreaterThan(0);
        var fresh = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        long beforePurgeLookup = catalog.RecordReadCount;
        catalog.FindDocument(parent, "item", fresh.Snapshot).ShouldBeNull();
        (catalog.RecordReadCount - beforePurgeLookup).ShouldBeLessThanOrEqualTo(3);
        catalog.CachedReferenceCount.ShouldBe(1);
        await coordinator.RollbackAsync(fresh);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Documents.Catalog] - Rollback: many own versions restore the prior head with constant reads (#1281)")]
    [InlineData(32)]
    [InlineData(256)]
    public async Task FindDocument_RolledBackOwnHistory_ShouldBoundCurrentRecordReads(int versions)
    {
        // Arrange
        await using var fixture = await Fixture.Create();
        var coordinator = fixture.Coordinator;
        var catalog = fixture.Catalog;
        var parent = fixture.Collection.Id;
        await fixture.Write("item", "{}");
        var pinned = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var original = catalog.FindDocument(parent, "item", pinned.Snapshot);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        for (int version = 0; version < versions; version++)
        {
            await fixture.WriteIn(writer, "item", "{}");
        }
        await coordinator.RollbackAsync(writer);
        var reader = await coordinator.BeginAsync(IsolationLevel.Snapshot);

        // Act
        long reads = long.MaxValue;
        for (int round = 0; round < 3; round++)
        {
            long before = catalog.RecordReadCount;
            catalog.FindDocument(parent, "item", reader.Snapshot).ShouldBe(original);
            (catalog.RecordReadCount - before).ShouldBeLessThanOrEqualTo(3);
            reads = Math.Min(reads, catalog.RecordReadCount - before);
        }

        // Assert
        reads.ShouldBe(3);
        catalog.FindDocument(parent, "item", pinned.Snapshot).ShouldBe(original);
        await coordinator.RollbackAsync(reader);
        await coordinator.RollbackAsync(pinned);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Documents.Catalog] - Lookup: writer order survives late publication, reopen and same-writer rollback (#1281)")]
    public async Task FindDocument_OutOfOrderPublication_ShouldPreserveVersionSelection()
    {
        // Arrange: a read-committed writer begins first but replaces a later writer.
        await using var fixture = await Fixture.Create();
        var earlier = await fixture.Coordinator.BeginAsync(IsolationLevel.ReadCommitted);
        var later = await fixture.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        await fixture.WriteIn(later, "item", "{\"n\":2}");
        await fixture.Coordinator.CommitAsync(later);
        var oldReader = await fixture.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        var oldVersion = fixture.Catalog.FindDocument(fixture.Collection.Id, "item", oldReader.Snapshot);
        await fixture.WriteIn(earlier, "item", "{\"n\":1}");
        await fixture.Coordinator.CommitAsync(earlier);
        var reader = await fixture.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        var expected = fixture.Catalog.FindDocument(fixture.Collection.Id, "item", reader.Snapshot);

        // Act
        var reopened = DocumentCatalog.Open(fixture.Storage, fixture.Coordinator);
        var selected = reopened.FindDocument(fixture.Collection.Id, "item", reader.Snapshot);
        reopened.FindDocument(fixture.Collection.Id, "item", oldReader.Snapshot).ShouldBe(oldVersion);
        reopened.FindDocument(fixture.Collection.Id, "item", reader.Snapshot).ShouldBe(selected);
        fixture.Catalog.FindDocument(fixture.Collection.Id, "item", oldReader.Snapshot).ShouldBe(oldVersion);
        fixture.Catalog.FindDocument(fixture.Collection.Id, "item", reader.Snapshot).ShouldBe(selected);
        var writer = await fixture.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        for (int version = 0; version < 20; version++) { await fixture.WriteIn(writer, "item", "{\"n\":3}"); }
        var own = fixture.Catalog.FindDocument(fixture.Collection.Id, "item", writer.Snapshot);
        await fixture.Coordinator.RollbackAsync(writer);

        // Assert
        fixture.Catalog.FindDocument(fixture.Collection.Id, "item", oldReader.Snapshot).ShouldBe(oldVersion);
        selected.ShouldBe(expected);
        fixture.Storage.ReadContent(new Storage.DocumentContentReference(selected!.Value.HeadLocation, selected.Value.Length, selected.Value.Checksum))
            .Span.SequenceEqual(System.Text.Encoding.UTF8.GetBytes("{\"n\":1}")).ShouldBeTrue();
        own!.Value.Version.ShouldBeGreaterThan(expected!.Value.Version);
        fixture.Catalog.FindDocument(fixture.Collection.Id, "item", reader.Snapshot).ShouldBe(expected);
        await fixture.Coordinator.RollbackAsync(reader);
        await fixture.Coordinator.RollbackAsync(oldReader);
    }
}
