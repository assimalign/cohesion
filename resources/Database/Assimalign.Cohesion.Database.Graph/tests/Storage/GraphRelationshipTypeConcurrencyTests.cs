using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Transactions;

using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Graph.Storage.Tests;

[CollectionDefinition(nameof(GraphRelationshipTypeConcurrencyTests), DisableParallelization = true)]
[Collection(nameof(GraphRelationshipTypeConcurrencyTests))]
public sealed class GraphRelationshipTypeConcurrencyTests
{
    [Fact(DisplayName = "Cohesion Test [Graph.Storage] - Type postings: pinned readers race replacement, deletion, commit, rollback and purge across cursor batches (#1633)")]
    public async Task ScanRelationshipTypes_ConcurrentMutations_ShouldKeepCompleteSnapshotAsync()
    {
        // Arrange: exceed two 64-entry cursor batches, with unrelated type postings present.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var storage = GraphStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "type-race");
        await using var coordinator = new TransactionCoordinator(storage, storage.WriteAheadJournal, storage.Records);
        var store = GraphStore.Open(storage, coordinator);
        var seed = await coordinator.BeginAsync(IsolationLevel.Snapshot, timeout.Token);
        var empty = new Dictionary<string, object?>();
        var node = await store.CreateNodeAsync(new StoredGraphNode((ulong)storage.ReserveTransactionSequence(), [], empty),
            TestLabelIds.All, seed, timeout.Token);
        const int relationshipCount = 130;
        Guid type = Guid.NewGuid(), noiseType = Guid.NewGuid();
        var edges = new List<StoredGraphRelationship>();
        for (int i = 0; i < relationshipCount; i++)
        {
            edges.Add(await store.CreateRelationshipAsync(new StoredGraphRelationship((ulong)storage.ReserveTransactionSequence(),
                node.Id, node.Id, "R", new Dictionary<string, object?> { ["x"] = 0 }), type, seed, timeout.Token));
            await store.CreateRelationshipAsync(new StoredGraphRelationship((ulong)storage.ReserveTransactionSequence(),
                node.Id, node.Id, "Noise", empty), noiseType, seed, timeout.Token);
        }
        await coordinator.CommitAsync(seed, timeout.Token);
        var old = await coordinator.BeginAsync(IsolationLevel.Snapshot, timeout.Token);
        int reading = 0, writing = 0, pauses = 0, updates = 0, deletes = 0;
        int readOverlaps = 0, writeOverlaps = 0, commits = 0, rollbacks = 0, purges = 0;
        using var phases = new Barrier(2);
        store.OnRelationshipTypeEntryRead = () =>
        {
            // The posting location is captured and the tree latch released, but ReadAt has
            // not rechecked the record. Mutate this exact identity before letting it proceed.
            Interlocked.Increment(ref pauses);
            phases.SignalAndWait(timeout.Token);
            if (Volatile.Read(ref writing) != 0) { Interlocked.Increment(ref readOverlaps); }
            phases.SignalAndWait(timeout.Token);
        };
        long before = store.RelationshipTypeEntriesVisited;

        // Act: each posting has a scripted interleaving; counters, not elapsed time, decide coverage.
        var reader = DedicatedTestTask.Run(() =>
        {
            Volatile.Write(ref reading, 1);
            try
            {
                var found = store.ScanRelationshipTypes(["R"], old.Snapshot, timeout.Token);
                found.Select(edge => edge.Id).ShouldBe(edges.Select(edge => edge.Id));
                foreach (var edge in found)
                {
                    edge.Properties["x"].ShouldBe(0);
                    edge.Properties.Count.ShouldBe(1);
                }
                return found;
            }
            catch { timeout.Cancel(); throw; }
            finally { Volatile.Write(ref reading, 0); }
        }, timeout.Token);
        var writer = DedicatedTestTask.Run(async () =>
        {
            try
            {
                for (int i = 0; i < relationshipCount; i++)
                {
                    Volatile.Write(ref writing, 1);
                    phases.SignalAndWait(timeout.Token);
                    var context = await coordinator.BeginAsync(IsolationLevel.Snapshot, timeout.Token);
                    try
                    {
                        // Growing properties force a replacement at a new physical location.
                        var replacement = await store.UpdateRelationshipAsync(edges[i].Id,
                            new Dictionary<string, object?> { ["x"] = i + 1, ["padding"] = new string('x', 512) }, context, timeout.Token);
                        replacement.ShouldNotBeNull().Properties["x"].ShouldBe(i + 1);
                        Interlocked.Increment(ref updates);
                        if (i % 4 >= 2)
                        {
                            await store.DeleteRelationshipAsync(edges[i].Id, context, timeout.Token);
                            store.FindRelationship(edges[i].Id, context.Snapshot).ShouldBeNull();
                            Interlocked.Increment(ref deletes);
                        }
                        if (Volatile.Read(ref reading) != 0) { Interlocked.Increment(ref writeOverlaps); }
                        if (i % 2 == 0)
                        { await coordinator.CommitAsync(context, timeout.Token); Interlocked.Increment(ref commits); }
                        else
                        { await coordinator.RollbackAsync(context, timeout.Token); Interlocked.Increment(ref rollbacks); }
                        coordinator.RunVersionPurgePass(timeout.Token);
                        Interlocked.Increment(ref purges);
                        phases.SignalAndWait(timeout.Token);
                    }
                    finally
                    {
                        if (context.State == TransactionState.Active) { await coordinator.RollbackAsync(context, CancellationToken.None); }
                        Volatile.Write(ref writing, 0);
                    }
                }
            }
            catch { timeout.Cancel(); throw; }
        }, timeout.Token);
        try
        {
            await Task.WhenAll(reader, writer);

            // Assert: exact ordered identities also reject duplicates and omissions. No writer
            // performs a type scan, so the visit delta belongs solely to the pinned reader.
            (await reader).Count.ShouldBe(relationshipCount);
            pauses.ShouldBe(relationshipCount);
            (store.RelationshipTypeEntriesVisited - before).ShouldBe(relationshipCount);
            updates.ShouldBe(relationshipCount);
            deletes.ShouldBe(64);
            readOverlaps.ShouldBe(relationshipCount);
            writeOverlaps.ShouldBe(relationshipCount);
            commits.ShouldBe(65);
            rollbacks.ShouldBe(65);
            purges.ShouldBe(relationshipCount);
            store.OnRelationshipTypeEntryRead = null;
            var current = await coordinator.BeginAsync(IsolationLevel.Snapshot, timeout.Token);
            try
            {
                var found = store.ScanRelationshipTypes(["R"], current.Snapshot, timeout.Token);
                found.Select(edge => edge.Id).ShouldBe(edges.Where((_, i) => i % 4 != 2).Select(edge => edge.Id));
                foreach (var edge in found)
                {
                    int index = edges.FindIndex(item => item.Id == edge.Id);
                    edge.Properties["x"].ShouldBe(index % 4 == 0 ? index + 1 : 0);
                }
            }
            finally { await coordinator.RollbackAsync(current, CancellationToken.None); }
        }
        finally
        {
            store.OnRelationshipTypeEntryRead = null;
            await coordinator.RollbackAsync(old, CancellationToken.None);
        }
        coordinator.RunVersionPurgePass(timeout.Token).ShouldBeGreaterThan(0);
    }
}
