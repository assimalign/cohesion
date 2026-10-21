using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Storage.Tests;

public sealed partial class GraphStoreTests
{
    [Fact(DisplayName = "Cohesion Test [Graph.Storage] - Update: identical replacements publish versions and numeric type changes retain rollback (#1149)")]
    public async Task Update_IdenticalAndNumericTypeChanges_ShouldPublishAndPreserveExactTypes()
    {
        // Arrange
        await using var fixture = new Fixture();
        var seed = await fixture.Begin();
        await fixture.Store.CreateIndexAsync("N", "key", seed);
        var node = await fixture.Store.CreateNodeAsync(new StoredGraphNode((ulong)fixture.Storage.ReserveTransactionSequence(), ["N"], Properties("key", 1)), TestLabelIds.All, seed);
        await fixture.Coordinator.CommitAsync(seed);
        var update = await fixture.Begin();

        // Act / Assert
        await fixture.Store.UpdateNodeAsync(node.Id, ["N", "N"], Properties("key", 1), TestLabelIds.All, update);
        CountVersions().ShouldBe(2);
        await fixture.Store.UpdateNodeAsync(node.Id, ["N"], Properties("key", 1d), TestLabelIds.All, update);
        CountVersions().ShouldBe(3);
        fixture.Store.FindNode(node.Id, update.Snapshot)!.Value.Properties["key"].ShouldBeOfType<double>();
        await fixture.Store.UpdateNodeAsync(node.Id, ["N"], Properties("key", 1), TestLabelIds.All, update);
        CountVersions().ShouldBe(4);
        (await fixture.Store.SearchIndexAsync("N", "key", 1, update.Snapshot)).Single().Properties["key"].ShouldBeOfType<int>();
        await fixture.Coordinator.RollbackAsync(update);
        CountVersions().ShouldBe(1);

        int CountVersions()
        {
            int count = 0;
            using var records = fixture.Storage.GetUnitIterator(2);
            while (records.MoveNext()) { if (records.Current.Data.Span[16] == 1) { count++; } }
            return count;
        }
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Storage] - Format: earlier graph formats are refused with the recreation remedy (#1532)")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Open_EarlierFormat_ShouldRefuseBeforeRecovery(byte version)
    {
        // Arrange
        await using var fixture = new Fixture();
        var write = await fixture.Begin();
        await fixture.Store.CreateNodeAsync(new StoredGraphNode((ulong)fixture.Storage.ReserveTransactionSequence(), ["N"], _empty), TestLabelIds.All, write);
        await fixture.Coordinator.CommitAsync(write);
        using var units = fixture.Storage.GetUnitIterator(2);
        units.MoveNext().ShouldBeTrue();
        var unit = units.Current;
        byte[] bytes = unit.Data.ToArray();
        bytes[17] = version;
        using (var bracket = fixture.Storage.BeginTransaction())
        {
            fixture.Storage.UpdateEntry(bracket, unit.PageId, unit.SlotIndex, bytes);
            bracket.Commit();
        }

        // Act / Assert
        Should.Throw<StorageCorruptionException>(() => GraphStore.EnsureIndexFormat(fixture.Storage)).Message.ShouldContain("recreate the database", Case.Sensitive);
        Should.Throw<StorageCorruptionException>(() => GraphStore.Open(fixture.Storage, fixture.Coordinator)).Message.ShouldContain("format 4", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Storage] - Update: endpoint property changes do not conflict with relationship insertion (#1149)")]
    public async Task CreateRelationship_UpdatedEndpoint_ShouldCheckExistenceOnly()
    {
        // Arrange
        await using var fixture = new Fixture();
        var seed = await fixture.Begin();
        var node = await fixture.Store.CreateNodeAsync(new StoredGraphNode((ulong)fixture.Storage.ReserveTransactionSequence(), ["N"], Properties("key", 1)), TestLabelIds.All, seed);
        await fixture.Coordinator.CommitAsync(seed);
        var stale = await fixture.Begin();
        var update = await fixture.Begin();
        await fixture.Store.UpdateNodeAsync(node.Id, ["N"], Properties("key", 2), TestLabelIds.All, update);
        await fixture.Coordinator.CommitAsync(update);

        // Act
        var relationship = await fixture.Store.CreateRelationshipAsync(new StoredGraphRelationship((ulong)fixture.Storage.ReserveTransactionSequence(), node.Id, node.Id, "R", _empty), Guid.Parse("10000000-0000-0000-0000-000000000001"), stale);
        await fixture.Coordinator.CommitAsync(stale);

        // Assert
        var read = await fixture.Begin();
        (await fixture.Store.GetIncidentAsync(node.Id, read.Snapshot)).Single().Id.ShouldBe(relationship.Id);
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Storage] - Update: snapshots retain records, indexes and adjacency until purge (#1149)")]
    public async Task Update_CommittedVersions_ShouldPreserveSnapshotsAndSurvivePurge()
    {
        // Arrange
        await using var fixture = new Fixture();
        var seed = await fixture.Begin();
        await fixture.Store.CreateIndexAsync("N", "key", seed);
        var a = await fixture.Store.CreateNodeAsync(new StoredGraphNode((ulong)fixture.Storage.ReserveTransactionSequence(), ["N"], Properties("key", 1)), TestLabelIds.All, seed);
        var b = await fixture.Store.CreateNodeAsync(new StoredGraphNode((ulong)fixture.Storage.ReserveTransactionSequence(), ["N"], _empty), TestLabelIds.All, seed);
        var edge = await fixture.Store.CreateRelationshipAsync(new StoredGraphRelationship((ulong)fixture.Storage.ReserveTransactionSequence(), a.Id, b.Id, "R", Properties("x", 1)), Guid.Parse("10000000-0000-0000-0000-000000000001"), seed);
        var self = await fixture.Store.CreateRelationshipAsync(new StoredGraphRelationship((ulong)fixture.Storage.ReserveTransactionSequence(), a.Id, a.Id, "R", Properties("x", 1)), Guid.Parse("10000000-0000-0000-0000-000000000001"), seed);
        await fixture.Coordinator.CommitAsync(seed);
        var old = await fixture.Begin();
        var write = await fixture.Begin();

        // Act
        await fixture.Store.UpdateNodeAsync(a.Id, ["N", "New"], Properties("key", 2), TestLabelIds.All, write, CancellationToken.None);
        await fixture.Store.UpdateRelationshipAsync(edge.Id, Properties("x", 9), write, CancellationToken.None);
        await fixture.Store.UpdateRelationshipAsync(self.Id, Properties("x", 9), write, CancellationToken.None);
        await fixture.Coordinator.CommitAsync(write);

        // Assert
        fixture.Store.FindNode(a.Id, old.Snapshot)!.Value.Properties["key"].ShouldBe(1);
        fixture.Store.GetNodes("N", old.Snapshot).Single(node => node.Id == a.Id).Properties["key"].ShouldBe(1);
        fixture.Store.GetNodes("New", old.Snapshot).ShouldBeEmpty();
        (await fixture.Store.SearchIndexAsync("N", "key", 1, old.Snapshot)).Single().Id.ShouldBe(a.Id);
        (await fixture.Store.GetIncidentAsync(a.Id, old.Snapshot)).ShouldAllBe(r => Equals(r.Properties["x"], 1));
        fixture.Coordinator.RunVersionPurgePass(default).ShouldBe(0);
        var current = await fixture.Begin();
        fixture.Store.GetNodes("New", current.Snapshot).Single().Id.ShouldBe(a.Id);
        (await fixture.Store.SearchIndexAsync("N", "key", 1, current.Snapshot)).ShouldBeEmpty();
        (await fixture.Store.SearchIndexAsync("N", "key", 2, current.Snapshot)).Single().Id.ShouldBe(a.Id);
        (await fixture.Store.GetIncidentAsync(a.Id, current.Snapshot)).Count.ShouldBe(2);
        (await fixture.Store.GetIncidentAsync(b.Id, current.Snapshot)).Single().Properties["x"].ShouldBe(9);
        await fixture.Coordinator.RollbackAsync(old);
        await fixture.Coordinator.RollbackAsync(current);
        fixture.Coordinator.RunVersionPurgePass(default).ShouldBeGreaterThan(0);
        var after = await fixture.Begin();
        fixture.Store.FindNode(a.Id, after.Snapshot)!.Value.Properties["key"].ShouldBe(2);
        fixture.Store.FindRelationship(self.Id, after.Snapshot)!.Value.Properties["x"].ShouldBe(9);
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Storage] - Update: rollback restores old records and rejects stale writers (#1149)")]
    public async Task Update_RollbackAndConflict_ShouldRestoreAllEntries()
    {
        // Arrange
        await using var fixture = new Fixture();
        var seed = await fixture.Begin();
        await fixture.Store.CreateIndexAsync("N", "key", seed);
        var a = await fixture.Store.CreateNodeAsync(new StoredGraphNode((ulong)fixture.Storage.ReserveTransactionSequence(), ["N"], Properties("key", 1)), TestLabelIds.All, seed);
        var edge = await fixture.Store.CreateRelationshipAsync(new StoredGraphRelationship((ulong)fixture.Storage.ReserveTransactionSequence(), a.Id, a.Id, "R", Properties("x", 1)), Guid.Parse("10000000-0000-0000-0000-000000000001"), seed);
        await fixture.Coordinator.CommitAsync(seed);
        var stale = await fixture.Begin();
        var write = await fixture.Begin();

        // Act
        await fixture.Store.UpdateNodeAsync(a.Id, [], Properties("key", 2), TestLabelIds.All, write);
        await fixture.Store.UpdateRelationshipAsync(edge.Id, Properties("x", 2), write);
        var created = await fixture.Store.CreateNodeAsync(new StoredGraphNode((ulong)fixture.Storage.ReserveTransactionSequence(), ["N"], _empty), TestLabelIds.All, write);
        await fixture.Store.UpdateNodeAsync(created.Id, ["N"], Properties("key", 3), TestLabelIds.All, write);
        await fixture.Coordinator.RollbackAsync(write);

        // Assert
        var again = await fixture.Begin();
        fixture.Store.FindNode(created.Id, again.Snapshot).ShouldBeNull();
        (await fixture.Store.SearchIndexAsync("N", "key", 1, again.Snapshot)).Single().Id.ShouldBe(a.Id);
        (await fixture.Store.GetIncidentAsync(a.Id, again.Snapshot)).Single().Properties["x"].ShouldBe(1);
        await fixture.Store.UpdateNodeAsync(a.Id, ["N"], Properties("key", 4), TestLabelIds.All, again);
        await fixture.Coordinator.CommitAsync(again);
        await Should.ThrowAsync<TransactionAbortedException>(() => fixture.Store.UpdateNodeAsync(a.Id, ["N"], Properties("key", 5), TestLabelIds.All, stale).AsTask());
        await fixture.Coordinator.RollbackAsync(stale);
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Storage] - Update: committed and in-flight versions recover (#1149)")]
    public async Task Update_CrashImage_ShouldRebuildChainsAndDiscardUncommittedUpdate()
    {
        // Arrange
        var data = new MemoryStream();
        var journal = new MemoryStream();
        using var storage = GraphStorage.Create(data, journal, new MemoryStream(), "updates");
        await using var coordinator = new TransactionCoordinator(storage, storage.WriteAheadJournal, storage.Records);
        var store = GraphStore.Open(storage, coordinator);
        var seed = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        await store.CreateIndexAsync("N", "key", seed);
        var a = await store.CreateNodeAsync(new StoredGraphNode((ulong)storage.ReserveTransactionSequence(), ["N"], Properties("key", "a")), TestLabelIds.All, seed);
        await coordinator.CommitAsync(seed);
        var write = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        await store.UpdateNodeAsync(a.Id, ["N"], Properties("key", "b"), TestLabelIds.All, write);
        await coordinator.CommitAsync(write);
        var partial = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        await store.UpdateNodeAsync(a.Id, ["N"], Properties("key", "b2"), TestLabelIds.All, partial);
        storage.WriteAheadJournal.Flush(forceDurable: false);

        // Act
        using var recovered = GraphStorage.Open(Clone(data), Clone(journal), new MemoryStream(), false);
        await using var recovery = new TransactionCoordinator(recovered, recovered.WriteAheadJournal, recovered.Records);
        GraphStore.EnsureIndexFormat(recovered);
        var plan = recovery.AnalyzeAndScrub();
        var reopened = GraphStore.Open(recovered, recovery);
        await reopened.RecoverIndexesAsync(plan.Aborted);
        recovery.CompleteRecovery();
        var read = await recovery.BeginAsync(IsolationLevel.Snapshot);

        // Assert
        reopened.FindNode(a.Id, read.Snapshot)!.Value.Properties["key"].ShouldBe("b");
        reopened.GetNodes("N", read.Snapshot).ShouldHaveSingleItem().Properties["key"].ShouldBe("b");
        (await reopened.SearchIndexAsync("N", "key", "b", read.Snapshot)).Single().Id.ShouldBe(a.Id);
        (await reopened.SearchIndexAsync("N", "key", "b2", read.Snapshot)).ShouldBeEmpty();
        await coordinator.RollbackAsync(partial);
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Storage] - Update: oversized replacements leave old versions intact (#1149)")]
    public async Task Update_OversizedReplacement_ShouldLeaveOldVersionIntact()
    {
        // Arrange
        await using var fixture = new Fixture();
        var write = await fixture.Begin();
        var a = await fixture.Store.CreateNodeAsync(new StoredGraphNode((ulong)fixture.Storage.ReserveTransactionSequence(), ["N"], Properties("key", 1)), TestLabelIds.All, write);
        await fixture.Store.CreateIndexAsync("N", "key", write);

        // Act
        await Should.ThrowAsync<GraphElementTooLargeException>(() => fixture.Store.UpdateNodeAsync(a.Id, ["N"], Properties("key", new string('x', 9000)), TestLabelIds.All, write).AsTask());
        await Should.ThrowAsync<GraphElementTooLargeException>(() => fixture.Store.UpdateNodeAsync(a.Id, ["N"], Properties("key", new string('x', 1100)), TestLabelIds.All, write).AsTask());

        // Assert
        fixture.Store.FindNode(a.Id, write.Snapshot)!.Value.Properties["key"].ShouldBe(1);
        (await fixture.Store.SearchIndexAsync("N", "key", 1, write.Snapshot)).Single().Id.ShouldBe(a.Id);
    }
}
