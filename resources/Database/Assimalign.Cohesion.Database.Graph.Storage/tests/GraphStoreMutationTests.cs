using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Storage.Tests;

[CollectionDefinition(nameof(GraphStoreMutationTests), DisableParallelization = true)]
[Collection(nameof(GraphStoreMutationTests))]
public sealed class GraphStoreMutationTests
{
    [Theory(DisplayName = "Cohesion Test [Graph.Storage] - Mutation: overlapping updates, deletes and duplicate creates serialize through publication (#1149)")]
    [InlineData(false, false, "Update")]
    [InlineData(false, true, "Update")]
    [InlineData(true, false, "Update")]
    [InlineData(true, true, "Update")]
    [InlineData(false, false, "Delete")]
    [InlineData(false, true, "Delete")]
    [InlineData(true, false, "Delete")]
    [InlineData(true, true, "Delete")]
    [InlineData(false, false, "Create")]
    [InlineData(false, true, "Create")]
    [InlineData(true, false, "Create")]
    [InlineData(true, true, "Create")]
    public async Task Mutate_OverlappingContextMutations_ShouldSerializePublication(bool relationship, bool useView, string mutation)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = timeout.Token;
        using var storage = GraphStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "overlap");
        await using var coordinator = new TransactionCoordinator(storage, storage.WriteAheadJournal, storage.Records);
        var store = GraphStore.Open(storage, coordinator);
        var elapsed = Stopwatch.StartNew();
        int overlaps = 0, encodings = 0;
        do
        {
            var seed = await coordinator.BeginAsync(IsolationLevel.Snapshot, token);
            if (overlaps == 0) { await store.CreateIndexAsync("N", "x", seed, token); }
            ulong nodeId = (ulong)storage.ReserveTransactionSequence();
            if (relationship || mutation != "Create")
            { await store.CreateNodeAsync(new StoredGraphNode(nodeId, ["N"], Properties(0)), seed, token); }
            ulong id = nodeId;
            if (relationship)
            {
                id = (ulong)storage.ReserveTransactionSequence();
                if (mutation != "Create")
                { await store.CreateRelationshipAsync(new StoredGraphRelationship(id, nodeId, nodeId, "R", Properties(0)), seed, token); }
            }
            await coordinator.CommitAsync(seed, token);
            var write = await coordinator.BeginAsync(IsolationLevel.Snapshot, token);
            // Exercise both the same context and distinct views of that transaction.
            var view = useView ? write.PinStatementSnapshot() : write;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int calls = 0;
            store.OnEncodingAsync = async () =>
            {
                encodings++;
                if (++calls == 1) { entered.SetResult(); await resume.Task.WaitAsync(token); }
            };

            // Act: the first write is suspended inside encoding, before publication.
            var first = WriteAsync(1, write);
            await entered.Task.WaitAsync(token);
            var second = mutation == "Delete" ? DeleteAsync(view) : WriteAsync(2, view);
            try { second.IsCompleted.ShouldBeFalse(); calls.ShouldBe(1); }
            finally
            {
                resume.TrySetResult();
                try
                {
                    await first.WaitAsync(token);
                    if (mutation == "Create") { await Should.ThrowAsync<ArgumentException>(() => second); }
                    else { await second.WaitAsync(token); }
                }
                finally { store.OnEncodingAsync = null; }
            }
            calls.ShouldBe(mutation == "Update" ? 2 : 1);
            await coordinator.CommitAsync(write, token);
            var read = await coordinator.BeginAsync(IsolationLevel.Snapshot, token);

            // Assert: every read path agrees, including after deleting the surviving version.
            CountLive(read.Snapshot).ShouldBe(mutation == "Delete" ? 0 : 1);
            int expected = mutation == "Create" ? 1 : 2;
            if (mutation == "Delete") { await AssertDeletedAsync(read.Snapshot); }
            else if (relationship)
            {
                store.FindRelationship(id, read.Snapshot)!.Value.Properties["x"].ShouldBe(expected);
                (await store.GetIncidentAsync(nodeId, read.Snapshot, token)).Single().Properties["x"].ShouldBe(expected);
            }
            else
            {
                store.FindNode(id, read.Snapshot)!.Value.Properties["x"].ShouldBe(expected);
                (await store.SearchIndexAsync("N", "x", expected == 1 ? 2 : 1, read.Snapshot, token)).ShouldBeEmpty();
                (await store.SearchIndexAsync("N", "x", expected, read.Snapshot, token)).Single().Id.ShouldBe(id);
            }
            await coordinator.RollbackAsync(read, token);
            var delete = await coordinator.BeginAsync(IsolationLevel.Snapshot, token);
            await DeleteAsync(delete);
            await coordinator.CommitAsync(delete, token);
            var after = await coordinator.BeginAsync(IsolationLevel.Snapshot, token);
            CountLive(after.Snapshot).ShouldBe(0);
            await AssertDeletedAsync(after.Snapshot);
            await coordinator.RollbackAsync(after, token);
            overlaps++;

            async Task WriteAsync(int value, TransactionContext context)
            {
                if (mutation == "Create")
                {
                    if (relationship) { await store.CreateRelationshipAsync(new StoredGraphRelationship(id, nodeId, nodeId, "R", Properties(value)), context, token); }
                    else { await store.CreateNodeAsync(new StoredGraphNode(id, ["N"], Properties(value)), context, token); }
                }
                else if (relationship) { await store.UpdateRelationshipAsync(id, Properties(value), context, token); }
                else { await store.UpdateNodeAsync(id, ["N"], Properties(value), context, token); }
            }

            async Task DeleteAsync(TransactionContext context)
            {
                if (relationship) { await store.DeleteRelationshipAsync(id, context, token); }
                else { await store.DeleteNodeAsync(id, false, context, token); }
            }

            async Task AssertDeletedAsync(TransactionSnapshot snapshot)
            {
                if (relationship)
                {
                    store.FindRelationship(id, snapshot).ShouldBeNull();
                    (await store.GetIncidentAsync(nodeId, snapshot, token)).ShouldBeEmpty();
                }
                else
                {
                    store.FindNode(id, snapshot).ShouldBeNull();
                    foreach (int value in new[] { 0, 1, 2 })
                    { (await store.SearchIndexAsync("N", "x", value, snapshot, token)).ShouldBeEmpty(); }
                }
            }

            int CountLive(TransactionSnapshot snapshot)
            {
                int count = 0;
                using var records = storage.GetUnitIterator(2);
                while (records.MoveNext())
                {
                    var bytes = records.Current.Data.Span;
                    if (bytes[16] != (relationship ? 2 : 1) || BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(18, 8)) != id) { continue; }
                    var (writer, deleter) = RecordVersionStamp.ReadStamps(bytes);
                    if (snapshot.IsVisible(writer) && (deleter == TransactionSequence.None || !snapshot.IsVisible(deleter))) { count++; }
                }
                return count;
            }
        } while ((elapsed.Elapsed < TimeSpan.FromMilliseconds(250) || overlaps < 2) && elapsed.Elapsed < TimeSpan.FromSeconds(10));
        overlaps.ShouldBeGreaterThanOrEqualTo(2);
        encodings.ShouldBe(overlaps * (mutation == "Update" ? 2 : 1));
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Storage] - Mutation: pinned views conflict while original ReadCommitted contexts refresh after waiting (#1149)")]
    [InlineData(false, false, IsolationLevel.ReadCommitted, true)]
    [InlineData(false, true, IsolationLevel.ReadCommitted, true)]
    [InlineData(true, false, IsolationLevel.ReadCommitted, true)]
    [InlineData(true, true, IsolationLevel.ReadCommitted, true)]
    [InlineData(false, false, IsolationLevel.Snapshot, true)]
    [InlineData(false, true, IsolationLevel.Snapshot, true)]
    [InlineData(true, false, IsolationLevel.Snapshot, true)]
    [InlineData(true, true, IsolationLevel.Snapshot, true)]
    [InlineData(false, false, IsolationLevel.ReadCommitted, false)]
    [InlineData(false, true, IsolationLevel.ReadCommitted, false)]
    [InlineData(true, false, IsolationLevel.ReadCommitted, false)]
    [InlineData(true, true, IsolationLevel.ReadCommitted, false)]
    public async Task Mutate_PinnedContextQueuedAfterUpdate_ShouldHonorIsolation(bool relationship, bool delete, IsolationLevel isolation, bool useView)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = timeout.Token;
        using var storage = GraphStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "snapshots");
        await using var coordinator = new TransactionCoordinator(storage, storage.WriteAheadJournal, storage.Records);
        var store = GraphStore.Open(storage, coordinator);
        var elapsed = Stopwatch.StartNew();
        int queued = 0, completed = 0, conflicts = 0;
        do
        {
            var seed = await coordinator.BeginAsync(IsolationLevel.Snapshot, token);
            ulong nodeId = (ulong)storage.ReserveTransactionSequence();
            await store.CreateNodeAsync(new StoredGraphNode(nodeId, ["N"], Properties(0)), seed, token);
            ulong id = nodeId;
            if (relationship)
            {
                id = (ulong)storage.ReserveTransactionSequence();
                await store.CreateRelationshipAsync(new StoredGraphRelationship(id, nodeId, nodeId, "R", Properties(0)), seed, token);
            }
            await coordinator.CommitAsync(seed, token);
            var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot, token);
            var waiting = await coordinator.BeginAsync(isolation, token);
            var pinned = useView ? waiting.PinStatementSnapshot() : waiting;
            object? original = relationship ? store.FindRelationship(id, pinned.Snapshot)?.Properties["x"] : store.FindNode(id, pinned.Snapshot)?.Properties["x"];
            original.ShouldBe(0);
            await UpdateAsync(writer, 1);

            // Act
            var pending = MutateAsync();
            pending.IsCompleted.ShouldBeFalse();
            queued++;
            await coordinator.CommitAsync(writer, token);

            // Assert
            bool expectConflict = useView || isolation == IsolationLevel.Snapshot;
            if (expectConflict)
            {
                await Should.ThrowAsync<TransactionAbortedException>(() => pending);
                await coordinator.RollbackAsync(waiting, token);
                conflicts++;
            }
            else
            {
                await pending.WaitAsync(token);
                await coordinator.CommitAsync(waiting, token);
                completed++;
            }
            var read = await coordinator.BeginAsync(IsolationLevel.Snapshot, token);
            object? value = relationship ? store.FindRelationship(id, read.Snapshot)?.Properties["x"] : store.FindNode(id, read.Snapshot)?.Properties["x"];
            value.ShouldBe(expectConflict ? 1 : delete ? null : 2);
            await coordinator.RollbackAsync(read, token);

            async Task UpdateAsync(TransactionContext context, int value)
            {
                if (relationship) { await store.UpdateRelationshipAsync(id, Properties(value), context, token); }
                else { await store.UpdateNodeAsync(id, ["N"], Properties(value), context, token); }
            }

            async Task MutateAsync()
            {
                if (!delete) { await UpdateAsync(pinned, 2); }
                else if (relationship) { await store.DeleteRelationshipAsync(id, pinned, token); }
                else { await store.DeleteNodeAsync(id, false, pinned, token); }
            }
        } while ((elapsed.Elapsed < TimeSpan.FromMilliseconds(250) || queued < 2) && elapsed.Elapsed < TimeSpan.FromSeconds(10));
        queued.ShouldBeGreaterThanOrEqualTo(2);
        completed.ShouldBe(isolation == IsolationLevel.ReadCommitted && !useView ? queued : 0);
        conflicts.ShouldBe(useView || isolation == IsolationLevel.Snapshot ? queued : 0);
    }

    private static IReadOnlyDictionary<string, object?> Properties(int value) => new Dictionary<string, object?> { ["x"] = value };
}
