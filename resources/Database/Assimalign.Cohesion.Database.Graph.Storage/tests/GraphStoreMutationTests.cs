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
    [Theory(DisplayName = "Cohesion Test [Graph.Storage] - Mutation: overlapping replacements serialize through publication and cannot resurrect on delete (#1149)")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Update_OverlappingContextMutations_ShouldKeepOneLiveVersionUntilDelete(bool relationship, bool useView)
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
            await store.CreateNodeAsync(new StoredGraphNode(nodeId, ["N"], Properties(0)), seed, token);
            ulong id = nodeId;
            if (relationship)
            {
                id = (ulong)storage.ReserveTransactionSequence();
                await store.CreateRelationshipAsync(new StoredGraphRelationship(id, nodeId, nodeId, "R", Properties(0)), seed, token);
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

            // Act: A has captured its prior version and is suspended inside encoding.
            var first = UpdateAsync(1, write);
            await entered.Task.WaitAsync(token);
            var second = UpdateAsync(2, view);
            try { second.IsCompleted.ShouldBeFalse(); calls.ShouldBe(1); }
            finally { resume.TrySetResult(); await Task.WhenAll(first, second).WaitAsync(token); store.OnEncodingAsync = null; }
            calls.ShouldBe(2);
            await coordinator.CommitAsync(write, token);
            var read = await coordinator.BeginAsync(IsolationLevel.Snapshot, token);

            // Assert: directory, physical versions and seeks all choose the second update.
            CountLive(read.Snapshot).ShouldBe(1);
            if (relationship)
            {
                store.FindRelationship(id, read.Snapshot)!.Value.Properties["x"].ShouldBe(2);
                (await store.GetIncidentAsync(nodeId, read.Snapshot, token)).Single().Properties["x"].ShouldBe(2);
            }
            else
            {
                store.FindNode(id, read.Snapshot)!.Value.Properties["x"].ShouldBe(2);
                (await store.SearchIndexAsync("N", "x", 1, read.Snapshot, token)).ShouldBeEmpty();
                (await store.SearchIndexAsync("N", "x", 2, read.Snapshot, token)).Single().Id.ShouldBe(id);
            }
            await coordinator.RollbackAsync(read, token);
            var delete = await coordinator.BeginAsync(IsolationLevel.Snapshot, token);
            if (relationship) { await store.DeleteRelationshipAsync(id, delete, token); }
            else { await store.DeleteNodeAsync(id, false, delete, token); }
            await coordinator.CommitAsync(delete, token);
            var after = await coordinator.BeginAsync(IsolationLevel.Snapshot, token);
            CountLive(after.Snapshot).ShouldBe(0);
            if (relationship)
            {
                store.FindRelationship(id, after.Snapshot).ShouldBeNull();
                (await store.GetIncidentAsync(nodeId, after.Snapshot, token)).ShouldBeEmpty();
            }
            else
            {
                store.FindNode(id, after.Snapshot).ShouldBeNull();
                foreach (int value in new[] { 0, 1, 2 })
                { (await store.SearchIndexAsync("N", "x", value, after.Snapshot, token)).ShouldBeEmpty(); }
            }
            await coordinator.RollbackAsync(after, token);
            overlaps++;

            async Task UpdateAsync(int value, TransactionContext context)
            {
                if (relationship) { await store.UpdateRelationshipAsync(id, Properties(value), context, token); }
                else { await store.UpdateNodeAsync(id, ["N"], Properties(value), context, token); }
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
        encodings.ShouldBe(overlaps * 2);
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Storage] - Mutation: a pinned ReadCommitted view refreshes after waiting while Snapshot conflicts (#1149)")]
    [InlineData(false, false, IsolationLevel.ReadCommitted)]
    [InlineData(false, true, IsolationLevel.ReadCommitted)]
    [InlineData(true, false, IsolationLevel.ReadCommitted)]
    [InlineData(true, true, IsolationLevel.ReadCommitted)]
    [InlineData(false, false, IsolationLevel.Snapshot)]
    [InlineData(false, true, IsolationLevel.Snapshot)]
    [InlineData(true, false, IsolationLevel.Snapshot)]
    [InlineData(true, true, IsolationLevel.Snapshot)]
    public async Task Mutate_PinnedContextQueuedAfterUpdate_ShouldHonorIsolation(bool relationship, bool delete, IsolationLevel isolation)
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
            var pinned = waiting.PinStatementSnapshot();
            await UpdateAsync(writer, 1);

            // Act
            var pending = MutateAsync();
            pending.IsCompleted.ShouldBeFalse();
            queued++;
            await coordinator.CommitAsync(writer, token);

            // Assert
            if (isolation == IsolationLevel.Snapshot)
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
            value.ShouldBe(isolation == IsolationLevel.Snapshot ? 1 : delete ? null : 2);
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
        completed.ShouldBe(isolation == IsolationLevel.ReadCommitted ? queued : 0);
        conflicts.ShouldBe(isolation == IsolationLevel.Snapshot ? queued : 0);
    }

    private static IReadOnlyDictionary<string, object?> Properties(int value) => new Dictionary<string, object?> { ["x"] = value };
}
