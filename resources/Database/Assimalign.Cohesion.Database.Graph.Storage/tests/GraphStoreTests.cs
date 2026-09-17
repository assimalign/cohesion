using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Storage.Tests;

public sealed class GraphStoreTests
{
    [Fact]
    public async Task RelationshipsPublishBothAdjacenciesAndRollbackRemovesAllEffects()
    {
        await using var fixture = new Fixture();
        var writer = await fixture.Begin();
        var a = await fixture.Store.CreateNodeAsync(["Person"], Properties("name", "Alice"), writer);
        var b = await fixture.Store.CreateNodeAsync(["Person"], Properties("name", "Bob"), writer);
        var relationship = await fixture.Store.CreateRelationshipAsync(a.Id, b.Id, "KNOWS", Properties("since", 2020), writer);
        (await fixture.Store.GetIncidentAsync(a.Id, writer.Snapshot)).Single().Id.ShouldBe(relationship.Id);
        (await fixture.Store.GetIncidentAsync(b.Id, writer.Snapshot)).Single().Id.ShouldBe(relationship.Id);
        var reader = await fixture.Begin();
        fixture.Store.FindNode(a.Id, reader.Snapshot).ShouldBeNull();
        (await fixture.Store.GetIncidentAsync(a.Id, reader.Snapshot)).ShouldBeEmpty();
        await fixture.Coordinator.RollbackAsync(writer);
        var next = await fixture.Begin();
        fixture.Store.GetNodes(null, next.Snapshot).ShouldBeEmpty();
        (await fixture.Store.GetIncidentAsync(a.Id, next.Snapshot)).ShouldBeEmpty();
    }

    [Fact]
    public async Task RestrictRefusesConnectedNodeAndDetachRollbackRestoresGraphAndIndex()
    {
        await using var fixture = new Fixture();
        var writer = await fixture.Begin();
        await fixture.Store.CreateIndexAsync("Person", "name", writer);
        var a = await fixture.Store.CreateNodeAsync(["Person"], Properties("name", "Alice"), writer);
        var b = await fixture.Store.CreateNodeAsync(["Person"], Properties("name", "Bob"), writer);
        var edge = await fixture.Store.CreateRelationshipAsync(a.Id, b.Id, "KNOWS", Empty, writer);
        var self = await fixture.Store.CreateRelationshipAsync(a.Id, a.Id, "SELF", Empty, writer);
        await fixture.Coordinator.CommitAsync(writer);
        var removing = await fixture.Begin();
        await Should.ThrowAsync<InvalidOperationException>(() => fixture.Store.DeleteNodeAsync(a.Id, false, removing).AsTask());
        (await fixture.Store.GetIncidentAsync(a.Id, removing.Snapshot)).Count.ShouldBe(2);
        await fixture.Store.DeleteNodeAsync(a.Id, true, removing);
        fixture.Store.FindNode(a.Id, removing.Snapshot).ShouldBeNull();
        fixture.Store.FindRelationship(edge.Id, removing.Snapshot).ShouldBeNull();
        fixture.Store.FindRelationship(self.Id, removing.Snapshot).ShouldBeNull();
        (await fixture.Store.GetIncidentAsync(b.Id, removing.Snapshot)).ShouldBeEmpty();
        (await fixture.Store.SearchIndexAsync("Person", "name", "Alice", removing.Snapshot)).ShouldBeEmpty();
        await fixture.Coordinator.RollbackAsync(removing);
        var reader = await fixture.Begin();
        fixture.Store.FindNode(a.Id, reader.Snapshot).ShouldNotBeNull();
        (await fixture.Store.GetIncidentAsync(a.Id, reader.Snapshot)).Count.ShouldBe(2);
        (await fixture.Store.SearchIndexAsync("Person", "name", "Alice", reader.Snapshot)).Single().Id.ShouldBe(a.Id);
    }

    [Fact]
    public async Task CommittedDetachPreservesOldSnapshotUntilVersionPurge()
    {
        await using var fixture = new Fixture();
        var write = await fixture.Begin();
        await fixture.Store.CreateIndexAsync("N", "key", write);
        var a = await fixture.Store.CreateNodeAsync(["N"], Properties("key", 1), write);
        var b = await fixture.Store.CreateNodeAsync(["N"], Properties("key", 2), write);
        var edge = await fixture.Store.CreateRelationshipAsync(a.Id, b.Id, "R", Empty, write);
        await fixture.Coordinator.CommitAsync(write);
        var old = await fixture.Begin();
        var remove = await fixture.Begin();
        await fixture.Store.DeleteNodeAsync(a.Id, true, remove);
        await fixture.Coordinator.CommitAsync(remove);
        fixture.Store.FindNode(a.Id, old.Snapshot).ShouldNotBeNull();
        (await fixture.Store.GetIncidentAsync(b.Id, old.Snapshot)).Single().Id.ShouldBe(edge.Id);
        (await fixture.Store.SearchIndexAsync("N", "key", 1L, old.Snapshot)).Single().Id.ShouldBe(a.Id);
        fixture.Coordinator.RunVersionPurgePass(default).ShouldBe(0);
        await fixture.Coordinator.CommitAsync(old);
        fixture.Coordinator.RunVersionPurgePass(default).ShouldBeGreaterThan(0);
        var current = await fixture.Begin();
        fixture.Store.FindNode(a.Id, current.Snapshot).ShouldBeNull();
        fixture.Store.FindNode(b.Id, current.Snapshot).ShouldNotBeNull();
        (await fixture.Store.GetIncidentAsync(b.Id, current.Snapshot)).ShouldBeEmpty();
    }

    [Fact]
    public async Task CrashRecoversCommittedRecordsAndIndexesAndDiscardsPartialTransaction()
    {
        var data = new MemoryStream();
        var journal = new MemoryStream();
        using var storage = GraphStorage.Create(data, journal, new MemoryStream(), "crash");
        await using var coordinator = new TransactionCoordinator(storage, storage.WriteAheadJournal, storage.Records);
        var store = GraphStore.Open(storage, coordinator);
        var write = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        await store.CreateIndexAsync("N", "key", write);
        var a = await store.CreateNodeAsync(["N"], Properties("key", 1), write);
        var b = await store.CreateNodeAsync(["N"], Properties("key", 2), write);
        var edge = await store.CreateRelationshipAsync(a.Id, b.Id, "R", Empty, write);
        await coordinator.CommitAsync(write);
        var partial = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        await store.DeleteNodeAsync(a.Id, true, partial);
        var uncommitted = await store.CreateNodeAsync(["N"], Properties("key", 3), partial);
        await store.CreateRelationshipAsync(b.Id, uncommitted.Id, "PARTIAL", Empty, partial);
        storage.WriteAheadJournal.Flush(true);

        using var recovered = GraphStorage.Open(Clone(data), Clone(journal), new MemoryStream(), false);
        await using var recovery = new TransactionCoordinator(recovered, recovered.WriteAheadJournal, recovered.Records);
        var plan = recovery.AnalyzeAndScrub();
        var recoveredStore = GraphStore.Open(recovered, recovery);
        await recoveredStore.RecoverIndexesAsync(plan.Aborted);
        recovery.CompleteRecovery();
        var reader = await recovery.BeginAsync(IsolationLevel.Snapshot);
        recoveredStore.GetNodes(null, reader.Snapshot).Select(node => node.Id).ShouldBe([a.Id, b.Id]);
        recoveredStore.FindNode(uncommitted.Id, reader.Snapshot).ShouldBeNull();
        (await recoveredStore.GetIncidentAsync(a.Id, reader.Snapshot)).Single().Id.ShouldBe(edge.Id);
        (await recoveredStore.GetIncidentAsync(b.Id, reader.Snapshot)).Single().Id.ShouldBe(edge.Id);
        (await recoveredStore.SearchIndexAsync("N", "key", 1m, reader.Snapshot)).Single().Id.ShouldBe(a.Id);
        (await recoveredStore.SearchIndexAsync("N", "key", 3m, reader.Snapshot)).ShouldBeEmpty();
        var newNode = await recoveredStore.CreateNodeAsync(["N"], Empty, reader);
        newNode.Id.ShouldBeGreaterThan(uncommitted.Id);
        await coordinator.RollbackAsync(partial);
    }

    [Fact]
    public async Task IndexBuildBackfillsExistingNodesAndNewWritesNormalizeNumericKeys()
    {
        await using var fixture = new Fixture();
        var write = await fixture.Begin();
        var a = await fixture.Store.CreateNodeAsync(["N"], Properties("key", 1), write);
        await fixture.Coordinator.CommitAsync(write);
        var index = await fixture.Begin();
        await fixture.Store.CreateIndexAsync("N", "key", index);
        var b = await fixture.Store.CreateNodeAsync(["N"], Properties("key", 1.0d), index);
        (await fixture.Store.SearchIndexAsync("N", "key", 1m, index.Snapshot)).Select(node => node.Id).ShouldBe([a.Id, b.Id]);
        await fixture.Coordinator.CommitAsync(index);
        var deleting = await fixture.Begin();
        await fixture.Store.DeleteNodeAsync(a.Id, false, deleting);
        await fixture.Coordinator.CommitAsync(deleting);
        var read = await fixture.Begin();
        (await fixture.Store.SearchIndexAsync("N", "key", 1L, read.Snapshot)).Single().Id.ShouldBe(b.Id);
    }

    [Fact]
    public async Task NumericIndexCandidatesPreserveExactIntegersAndExtremeFloatingPointValues()
    {
        await using var fixture = new Fixture();
        var writer = await fixture.Begin();
        var first = await fixture.Store.CreateNodeAsync(["N"], Properties("value", 9007199254740992L), writer);
        var second = await fixture.Store.CreateNodeAsync(["N"], Properties("value", 9007199254740993L), writer);
        var tiny = await fixture.Store.CreateNodeAsync(["N"], Properties("value", 1e-100), writer);
        await fixture.Store.CreateNodeAsync(["N"], Properties("value", 2e-100), writer);
        await fixture.Store.CreateIndexAsync("N", "value", writer);
        var huge = await fixture.Store.CreateNodeAsync(["N"], Properties("value", 1e100), writer);
        (await fixture.Store.SearchIndexAsync("N", "value", 9007199254740992L, writer.Snapshot)).Single().Id.ShouldBe(first.Id);
        (await fixture.Store.SearchIndexAsync("N", "value", 9007199254740993L, writer.Snapshot)).Single().Id.ShouldBe(second.Id);
        (await fixture.Store.SearchIndexAsync("N", "value", 9007199254740992d, writer.Snapshot)).Count.ShouldBe(2);
        (await fixture.Store.SearchIndexAsync("N", "value", 1e-100, writer.Snapshot)).Single().Id.ShouldBe(tiny.Id);
        (await fixture.Store.SearchIndexAsync("N", "value", 1e100, writer.Snapshot)).Single().Id.ShouldBe(huge.Id);
    }

    [Fact]
    public async Task IndexDropRollbackAndRecreationRetainSnapshotSemantics()
    {
        await using var fixture = new Fixture();
        var create = await fixture.Begin();
        await fixture.Store.CreateIndexAsync("N", "key", create);
        await fixture.Store.CreateNodeAsync(["N"], Properties("key", "value"), create);
        await fixture.Coordinator.CommitAsync(create);
        var old = await fixture.Begin();
        var drop = await fixture.Begin();
        await fixture.Store.DropIndexAsync("N", "key", drop);
        fixture.Store.HasIndex("N", "key", drop.Snapshot).ShouldBeFalse();
        fixture.Store.HasIndex("N", "key", old.Snapshot).ShouldBeTrue();
        await fixture.Coordinator.RollbackAsync(drop);
        var again = await fixture.Begin();
        fixture.Store.HasIndex("N", "key", again.Snapshot).ShouldBeTrue();
        await fixture.Store.DropIndexAsync("N", "key", again);
        await fixture.Store.CreateIndexAsync("N", "key", again);
        (await fixture.Store.SearchIndexAsync("N", "key", "value", again.Snapshot)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task StaleSnapshotCannotCreateRelationshipToDeletedEndpoint()
    {
        await using var fixture = new Fixture();
        var create = await fixture.Begin();
        var node = await fixture.Store.CreateNodeAsync(["N"], Empty, create);
        await fixture.Coordinator.CommitAsync(create);
        var stale = await fixture.Begin();
        var deleting = await fixture.Begin();
        await fixture.Store.DeleteNodeAsync(node.Id, true, deleting);
        await fixture.Coordinator.CommitAsync(deleting);
        await Should.ThrowAsync<TransactionAbortedException>(() => fixture.Store.CreateRelationshipAsync(node.Id, node.Id, "R", Empty, stale).AsTask());
        await fixture.Coordinator.RollbackAsync(stale);
        var missing = await fixture.Begin();
        await Should.ThrowAsync<InvalidOperationException>(() => fixture.Store.CreateRelationshipAsync(node.Id, node.Id, "R", Empty, missing).AsTask());
    }

    [Fact]
    public async Task StaleDetachCannotSilentlyDeleteNewlyCommittedRelationships()
    {
        await using var fixture = new Fixture();
        var create = await fixture.Begin();
        var node = await fixture.Store.CreateNodeAsync(["N"], Empty, create);
        await fixture.Coordinator.CommitAsync(create);
        var stale = await fixture.Begin();
        var linking = await fixture.Begin();
        await fixture.Store.CreateRelationshipAsync(node.Id, node.Id, "R", Empty, linking);
        await fixture.Coordinator.CommitAsync(linking);
        await Should.ThrowAsync<TransactionAbortedException>(() => fixture.Store.DeleteNodeAsync(node.Id, true, stale).AsTask());
    }

    [Fact]
    public async Task InvalidPropertyAndCancellationLeaveNoGraphRecords()
    {
        await using var fixture = new Fixture();
        var writer = await fixture.Begin();
        await Should.ThrowAsync<ArgumentException>(() => fixture.Store.CreateNodeAsync(["N"], Properties("bad", new object()), writer).AsTask());
        await Should.ThrowAsync<ArgumentException>(() => fixture.Store.CreateNodeAsync(["N"], Properties("tooBig", new string('x', 100_000)), writer).AsTask());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => fixture.Store.CreateNodeAsync(["N"], Empty, writer, cancellation.Token).AsTask());
        fixture.Store.GetNodes(null, writer.Snapshot).ShouldBeEmpty();
    }

    [Fact]
    public async Task CorruptNonFiniteStoredPropertyIsRejected()
    {
        await using var fixture = new Fixture();
        var writer = await fixture.Begin();
        var node = await fixture.Store.CreateNodeAsync(["N"], Properties("value", 1d), writer);
        await fixture.Coordinator.CommitAsync(writer);
        using var iterator = fixture.Storage.GetUnitIterator(2);
        iterator.MoveNext().ShouldBeTrue();
        var unit = iterator.Current;
        byte[] bytes = unit.Data.ToArray();
        BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(bytes.Length - 8), double.PositiveInfinity);
        using (var bracket = fixture.Storage.BeginTransaction())
        {
            fixture.Storage.UpdateEntry(bracket, unit.PageId, unit.SlotIndex, bytes);
            bracket.Commit();
        }
        var reader = await fixture.Begin();
        Should.Throw<StorageCorruptionException>(() => fixture.Store.FindNode(node.Id, reader.Snapshot));
    }

    [Fact]
    public async Task RolledBackWaiterReleasesItsLateWriterGrant()
    {
        await using var fixture = new Fixture();
        var holder = await fixture.Begin();
        await fixture.Store.CreateNodeAsync(["N"], Empty, holder);
        var waiter = await fixture.Begin();
        var pending = fixture.Store.CreateNodeAsync(["N"], Empty, waiter).AsTask();
        pending.IsCompleted.ShouldBeFalse();
        await fixture.Coordinator.RollbackAsync(waiter);
        await fixture.Coordinator.CommitAsync(holder);
        await Should.ThrowAsync<TransactionAbortedException>(() => pending);
        var next = await fixture.Begin();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await fixture.Store.CreateNodeAsync(["N"], Empty, next, limit.Token);
        fixture.Store.GetNodes(null, next.Snapshot).Count.ShouldBe(2);
    }

    [Fact]
    public async Task IndexAndAdjacencyRootSplitsSurviveRestart()
    {
        var data = new MemoryStream();
        var journal = new MemoryStream();
        using var storage = GraphStorage.Create(data, journal, new MemoryStream(), "splits");
        await using var coordinator = new TransactionCoordinator(storage, storage.WriteAheadJournal, storage.Records);
        var store = GraphStore.Open(storage, coordinator);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        await store.CreateIndexAsync("N", "key", writer);
        await store.CreateIndexAsync("N", "group", writer);
        var center = await store.CreateNodeAsync(["N"], Properties("key", -1), writer);
        for (int i = 0; i < 240; i++)
        {
            var node = await store.CreateNodeAsync(["N"], new Dictionary<string, object?> { ["key"] = i, ["group"] = "same" }, writer);
            await store.CreateRelationshipAsync(center.Id, node.Id, "R", Empty, writer);
        }
        await coordinator.CommitAsync(writer);
        using var recovered = GraphStorage.Open(Clone(data), Clone(journal), new MemoryStream());
        await using var recovery = new TransactionCoordinator(recovered, recovered.WriteAheadJournal, recovered.Records);
        var plan = recovery.AnalyzeAndScrub();
        var next = GraphStore.Open(recovered, recovery);
        await next.RecoverIndexesAsync(plan.Aborted);
        recovery.CompleteRecovery();
        var read = await recovery.BeginAsync(IsolationLevel.Snapshot);
        (await next.GetIncidentAsync(center.Id, read.Snapshot)).Count.ShouldBe(240);
        (await next.SearchIndexAsync("N", "key", 239L, read.Snapshot)).Count.ShouldBe(1);
        (await next.SearchIndexAsync("N", "key", 240L, read.Snapshot)).ShouldBeEmpty();
        (await next.SearchIndexAsync("N", "group", "same", read.Snapshot)).Count.ShouldBe(240);
    }

    private static readonly IReadOnlyDictionary<string, object?> Empty = new Dictionary<string, object?>();
    private static IReadOnlyDictionary<string, object?> Properties(string key, object? value) => new Dictionary<string, object?> { [key] = value };
    private static MemoryStream Clone(MemoryStream source)
    {
        var result = new MemoryStream();
        source.WriteTo(result);
        result.Position = 0;
        return result;
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal GraphStorage Storage { get; } = GraphStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "test");
        internal TransactionCoordinator Coordinator { get; }
        internal IGraphStore Store { get; }
        internal Fixture()
        {
            Coordinator = new TransactionCoordinator(Storage, Storage.WriteAheadJournal, Storage.Records);
            Store = GraphStore.Open(Storage, Coordinator);
        }
        internal ValueTask<ITransactionContext> Begin() => Coordinator.BeginAsync(IsolationLevel.Snapshot);
        public async ValueTask DisposeAsync()
        {
            await Coordinator.DisposeAsync();
            Storage.Dispose();
        }
    }
}
