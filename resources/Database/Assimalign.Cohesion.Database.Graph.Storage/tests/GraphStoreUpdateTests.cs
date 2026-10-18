using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Storage.Tests;

public sealed partial class GraphStoreTests
{
    [Fact(DisplayName = "Cohesion Test [Graph.Storage] - Update: snapshots retain records, indexes and adjacency until purge (#1149)")]
    public async Task Update_CommittedVersions_ShouldPreserveSnapshotsAndSurvivePurge()
    {
        // Arrange
        await using var fixture = new Fixture();
        var seed = await fixture.Begin();
        await fixture.Store.CreateIndexAsync("N", "key", seed);
        var a = await fixture.Store.CreateNodeAsync(["N"], Properties("key", 1), seed);
        var b = await fixture.Store.CreateNodeAsync(["N"], _empty, seed);
        var edge = await fixture.Store.CreateRelationshipAsync(a.Id, b.Id, "R", Properties("x", 1), seed);
        var self = await fixture.Store.CreateRelationshipAsync(a.Id, a.Id, "R", Properties("x", 1), seed);
        await fixture.Coordinator.CommitAsync(seed);
        var old = await fixture.Begin();
        var write = await fixture.Begin();

        // Act
        await fixture.Store.UpdateNodeAsync(a.Id, ["N", "New"], Properties("key", 2), write, CancellationToken.None);
        await fixture.Store.UpdateRelationshipAsync(edge.Id, Properties("x", 9), write, CancellationToken.None);
        await fixture.Store.UpdateRelationshipAsync(self.Id, Properties("x", 9), write, CancellationToken.None);
        await fixture.Coordinator.CommitAsync(write);

        // Assert
        fixture.Store.FindNode(a.Id, old.Snapshot)!.Value.Properties["key"].ShouldBe(1);
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
        var a = await fixture.Store.CreateNodeAsync(["N"], Properties("key", 1), seed);
        var edge = await fixture.Store.CreateRelationshipAsync(a.Id, a.Id, "R", Properties("x", 1), seed);
        await fixture.Coordinator.CommitAsync(seed);
        var stale = await fixture.Begin();
        var write = await fixture.Begin();

        // Act
        await fixture.Store.UpdateNodeAsync(a.Id, [], Properties("key", 2), write);
        await fixture.Store.UpdateRelationshipAsync(edge.Id, Properties("x", 2), write);
        var created = await fixture.Store.CreateNodeAsync(["N"], _empty, write);
        await fixture.Store.UpdateNodeAsync(created.Id, ["N"], Properties("key", 3), write);
        await fixture.Coordinator.RollbackAsync(write);

        // Assert
        var again = await fixture.Begin();
        fixture.Store.FindNode(created.Id, again.Snapshot).ShouldBeNull();
        (await fixture.Store.SearchIndexAsync("N", "key", 1, again.Snapshot)).Single().Id.ShouldBe(a.Id);
        (await fixture.Store.GetIncidentAsync(a.Id, again.Snapshot)).Single().Properties["x"].ShouldBe(1);
        await fixture.Store.UpdateNodeAsync(a.Id, ["N"], Properties("key", 4), again);
        await fixture.Coordinator.CommitAsync(again);
        await Should.ThrowAsync<TransactionAbortedException>(() => fixture.Store.UpdateNodeAsync(a.Id, ["N"], Properties("key", 5), stale).AsTask());
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
        var a = await store.CreateNodeAsync(["N"], Properties("key", "a"), seed);
        await coordinator.CommitAsync(seed);
        var write = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        await store.UpdateNodeAsync(a.Id, ["N"], Properties("key", "b"), write);
        await coordinator.CommitAsync(write);
        var partial = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        await store.UpdateNodeAsync(a.Id, ["N"], Properties("key", "b2"), partial);
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
        var a = await fixture.Store.CreateNodeAsync(["N"], Properties("key", 1), write);
        await fixture.Store.CreateIndexAsync("N", "key", write);

        // Act
        await Should.ThrowAsync<GraphElementTooLargeException>(() => fixture.Store.UpdateNodeAsync(a.Id, ["N"], Properties("key", new string('x', 9000)), write).AsTask());
        await Should.ThrowAsync<GraphElementTooLargeException>(() => fixture.Store.UpdateNodeAsync(a.Id, ["N"], Properties("key", new string('x', 1100)), write).AsTask());

        // Assert
        fixture.Store.FindNode(a.Id, write.Snapshot)!.Value.Properties["key"].ShouldBe(1);
        (await fixture.Store.SearchIndexAsync("N", "key", 1, write.Snapshot)).Single().Id.ShouldBe(a.Id);
    }
}
