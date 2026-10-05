using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Indexing.Tests.TestObjects;
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
        var edge = await fixture.Store.CreateRelationshipAsync(a.Id, b.Id, "KNOWS", _empty, writer);
        var self = await fixture.Store.CreateRelationshipAsync(a.Id, a.Id, "SELF", _empty, writer);
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
        var edge = await fixture.Store.CreateRelationshipAsync(a.Id, b.Id, "R", _empty, write);
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
        var edge = await store.CreateRelationshipAsync(a.Id, b.Id, "R", _empty, write);
        await coordinator.CommitAsync(write);
        var partial = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        await store.DeleteNodeAsync(a.Id, true, partial);
        var uncommitted = await store.CreateNodeAsync(["N"], Properties("key", 3), partial);
        await store.CreateRelationshipAsync(b.Id, uncommitted.Id, "PARTIAL", _empty, partial);
        // Drain journal buffering into the in-memory recovery image; MemoryStream
        // has no durable-flush contract.
        storage.WriteAheadJournal.Flush(forceDurable: false);

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
        var newNode = await recoveredStore.CreateNodeAsync(["N"], _empty, reader);
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
        var node = await fixture.Store.CreateNodeAsync(["N"], _empty, create);
        await fixture.Coordinator.CommitAsync(create);
        var stale = await fixture.Begin();
        var deleting = await fixture.Begin();
        await fixture.Store.DeleteNodeAsync(node.Id, true, deleting);
        await fixture.Coordinator.CommitAsync(deleting);
        await Should.ThrowAsync<TransactionAbortedException>(() => fixture.Store.CreateRelationshipAsync(node.Id, node.Id, "R", _empty, stale).AsTask());
        await fixture.Coordinator.RollbackAsync(stale);
        var missing = await fixture.Begin();
        await Should.ThrowAsync<InvalidOperationException>(() => fixture.Store.CreateRelationshipAsync(node.Id, node.Id, "R", _empty, missing).AsTask());
    }

    [Fact]
    public async Task StaleDetachCannotSilentlyDeleteNewlyCommittedRelationships()
    {
        await using var fixture = new Fixture();
        var create = await fixture.Begin();
        var node = await fixture.Store.CreateNodeAsync(["N"], _empty, create);
        await fixture.Coordinator.CommitAsync(create);
        var stale = await fixture.Begin();
        var linking = await fixture.Begin();
        await fixture.Store.CreateRelationshipAsync(node.Id, node.Id, "R", _empty, linking);
        await fixture.Coordinator.CommitAsync(linking);
        await Should.ThrowAsync<TransactionAbortedException>(() => fixture.Store.DeleteNodeAsync(node.Id, true, stale).AsTask());
    }

    [Fact]
    public async Task InvalidPropertyAndCancellationLeaveNoGraphRecords()
    {
        await using var fixture = new Fixture();
        var writer = await fixture.Begin();
        await Should.ThrowAsync<ArgumentException>(() => fixture.Store.CreateNodeAsync(["N"], Properties("bad", new object()), writer).AsTask());
        await Should.ThrowAsync<GraphElementTooLargeException>(() => fixture.Store.CreateNodeAsync(["N"], Properties("tooBig", new string('x', 100_000)), writer).AsTask());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => fixture.Store.CreateNodeAsync(["N"], _empty, writer, cancellation.Token).AsTask());
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
        await fixture.Store.CreateNodeAsync(["N"], _empty, holder);
        var waiter = await fixture.Begin();
        var pending = fixture.Store.CreateNodeAsync(["N"], _empty, waiter).AsTask();
        pending.IsCompleted.ShouldBeFalse();
        await fixture.Coordinator.RollbackAsync(waiter);
        await fixture.Coordinator.CommitAsync(holder);
        await Should.ThrowAsync<TransactionAbortedException>(() => pending);
        var next = await fixture.Begin();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await fixture.Store.CreateNodeAsync(["N"], _empty, next, limit.Token);
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
            await store.CreateRelationshipAsync(center.Id, node.Id, "R", _empty, writer);
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

    [Fact(DisplayName = "Cohesion Test [Graph.Storage] - Element size: an oversized record or indexed value is refused before anything is written")]
    public async Task CreateAsync_OversizedRecordOrIndexedValue_ShouldThrowAndWriteNothing()
    {
        // Arrange
        await using var fixture = new Fixture();
        var writer = await fixture.Begin();
        await fixture.Store.CreateIndexAsync("N", "key", writer);
        string[] labels = Enumerable.Range(0, 300).Select(i => "Label_with_a_fairly_long_name_" + i.ToString("D3", CultureInfo.InvariantCulture)).ToArray();

        // Act
        var record = await Should.ThrowAsync<GraphElementTooLargeException>(() => fixture.Store.CreateNodeAsync(labels, _empty, writer).AsTask());
        var property = await Should.ThrowAsync<GraphElementTooLargeException>(() =>
            fixture.Store.CreateNodeAsync(["M"], Properties("s", new string('s', 9_000)), writer).AsTask());
        var key = await Should.ThrowAsync<GraphElementTooLargeException>(() =>
            fixture.Store.CreateNodeAsync(["N"], Properties("key", new string('k', 600)), writer).AsTask());
        var node = await fixture.Store.CreateNodeAsync(["N"], Properties("key", "fits"), writer);
        var edge = await Should.ThrowAsync<GraphElementTooLargeException>(() =>
            fixture.Store.CreateRelationshipAsync(node.Id, node.Id, "R", Properties("s", new string('s', 9_000)), writer).AsTask());

        // Assert
        record.Message.ShouldStartWith("A node's labels and properties encode to ", Case.Sensitive);
        property.Message.ShouldEndWith("more than the 8092 bytes one graph record can hold.", Case.Sensitive);
        key.Message.ShouldContain("1016-byte index key", Case.Sensitive);
        edge.Message.ShouldStartWith("A relationship's type and properties encode to ", Case.Sensitive);
        fixture.Store.GetNodes(null, writer.Snapshot).ShouldHaveSingleItem().Id.ShouldBe(node.Id);
        (await fixture.Store.GetIncidentAsync(node.Id, writer.Snapshot)).ShouldBeEmpty();
        (await fixture.Store.SearchIndexAsync("N", "key", "fits", writer.Snapshot)).ShouldHaveSingleItem().Id.ShouldBe(node.Id);
        await fixture.Coordinator.CommitAsync(writer);
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Storage] - Element size: an index build over an oversized value is refused, and a search for one matches nothing")]
    public async Task CreateIndexAsync_OversizedExistingValue_ShouldThrowAndSearchShouldMatchNothing()
    {
        // Arrange
        await using var fixture = new Fixture();
        var writer = await fixture.Begin();
        string tooLong = new('k', 600);
        await fixture.Store.CreateNodeAsync(["Long"], Properties("key", tooLong), writer);
        await fixture.Store.CreateNodeAsync(["N"], Properties("key", "short"), writer);
        await fixture.Store.CreateIndexAsync("N", "key", writer);

        // Act
        var build = await Should.ThrowAsync<GraphElementTooLargeException>(() => fixture.Store.CreateIndexAsync("Long", "key", writer).AsTask());
        var search = await fixture.Store.SearchIndexAsync("N", "key", tooLong, writer.Snapshot);

        // Assert
        build.Message.ShouldContain("1016-byte index key", Case.Sensitive);
        search.ShouldBeEmpty();
        fixture.Store.HasIndex("Long", "key", writer.Snapshot).ShouldBeFalse();
        fixture.Store.GetIndexes(writer.Snapshot).ShouldBe([new StoredGraphIndex("N", "key")]);
        await Should.ThrowAsync<ArgumentException>(() => fixture.Store.SearchIndexAsync("N", "key", new object(), writer.Snapshot).AsTask());
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Storage] - Indexes: GetIndexes lists exactly the indexes a snapshot sees, across restart")]
    public async Task GetIndexes_AcrossSnapshotsAndRestart_ShouldListVisibleIndexes()
    {
        // Arrange
        var data = new MemoryStream();
        var journal = new MemoryStream();
        using var storage = GraphStorage.Create(data, journal, new MemoryStream(), "indexes");
        await using var coordinator = new TransactionCoordinator(storage, storage.WriteAheadJournal, storage.Records);
        var store = GraphStore.Open(storage, coordinator);
        var create = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        await store.CreateNodeAsync(["N"], Properties("key", 1), create);
        await store.CreateIndexAsync("N", "key", create);
        await store.CreateIndexAsync("N", "other", create);
        await store.CreateIndexAsync("M", "key", create);
        await coordinator.CommitAsync(create);
        var old = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var drop = await coordinator.BeginAsync(IsolationLevel.Snapshot);

        // Act
        await store.DropIndexAsync("N", "other", drop);
        var dropping = store.GetIndexes(drop.Snapshot);
        var before = store.GetIndexes(old.Snapshot);
        await coordinator.CommitAsync(drop);
        await coordinator.RollbackAsync(old);
        using var recovered = GraphStorage.Open(Clone(data), Clone(journal), new MemoryStream());
        await using var recovery = new TransactionCoordinator(recovered, recovered.WriteAheadJournal, recovered.Records);
        var plan = recovery.AnalyzeAndScrub();
        var reopened = GraphStore.Open(recovered, recovery);
        await reopened.RecoverIndexesAsync(plan.Aborted);
        recovery.CompleteRecovery();
        var read = await recovery.BeginAsync(IsolationLevel.Snapshot);
        var afterRestart = reopened.GetIndexes(read.Snapshot);

        // Assert
        dropping.ShouldBe([new StoredGraphIndex("N", "key"), new StoredGraphIndex("M", "key")], ignoreOrder: true);
        before.ShouldBe([new StoredGraphIndex("N", "key"), new StoredGraphIndex("N", "other"), new StoredGraphIndex("M", "key")], ignoreOrder: true);
        afterRestart.ShouldBe(dropping, ignoreOrder: true);
    }

    private static readonly IReadOnlyDictionary<string, object?> _empty = new Dictionary<string, object?>();
    private static IReadOnlyDictionary<string, object?> Properties(string key, object? value) => new Dictionary<string, object?> { [key] = value };
    private static MemoryStream Clone(MemoryStream source)
    {
        var result = new MemoryStream();
        source.WriteTo(result);
        result.Position = 0;
        return result;
    }
    [Fact(DisplayName = "Cohesion Test [Database.Graph.Storage] - Format: EnsureIndexFormat passes current trees and refuses trees in B-tree page format 1, as Open does (#1194)")]
    public async Task EnsureIndexFormat_TreesInFormatOne_ShouldRefuse()
    {
        // Arrange: a property index over a node, committed.
        await using var fixture = new Fixture();
        var writer = await fixture.Begin();
        await fixture.Store.CreateIndexAsync("Person", "name", writer);
        await fixture.Store.CreateNodeAsync(["Person"], Properties("name", "Alice"), writer);
        await fixture.Coordinator.CommitAsync(writer);
        GraphStore.EnsureIndexFormat(fixture.Storage);

        // Act: the trees rewritten into the layout engines before #1194 wrote.
        LegacyBTreePages.DowngradeIndexPages(fixture.Storage).ShouldBeGreaterThan(0);

        // Assert
        var refusal = Should.Throw<IndexFormatException>(() => GraphStore.EnsureIndexFormat(fixture.Storage));
        refusal.FoundVersion.ShouldBe(1);
        refusal.Message.ShouldContain("uses B-tree page format 1, but this engine supports only format 2", Case.Sensitive);
        Should.Throw<IndexFormatException>(() => GraphStore.Open(fixture.Storage, fixture.Coordinator)).FoundVersion.ShouldBe(1);
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
        internal ValueTask<TransactionContext> Begin() => Coordinator.BeginAsync(IsolationLevel.Snapshot);
        public async ValueTask DisposeAsync()
        {
            await Coordinator.DisposeAsync();
            Storage.Dispose();
        }
    }
}
