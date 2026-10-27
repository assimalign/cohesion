using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Transactions;

using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Graph.Storage.Tests;

[CollectionDefinition(nameof(GraphLabelConcurrencyTests), DisableParallelization = true)]
[Collection(nameof(GraphLabelConcurrencyTests))]
public sealed class GraphLabelConcurrencyTests
{
    [Fact(DisplayName = "Cohesion Test [Graph.Storage] - Labels: pinned readers race replacement, commit, rollback and purge (#1534)")]
    public async Task GetNodes_ConcurrentMutations_ShouldKeepCompleteSnapshotsAsync()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var storage = GraphStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "label-race");
        await using var coordinator = new TransactionCoordinator(storage, storage.WriteAheadJournal, storage.Records);
        var store = GraphStore.Open(storage, coordinator);
        var seed = await coordinator.BeginAsync(IsolationLevel.Snapshot, timeout.Token);
        var node = await store.CreateNodeAsync(new StoredGraphNode((ulong)storage.ReserveTransactionSequence(), ["N"], Properties(0)),
            TestLabelIds.All, seed, timeout.Token);
        for (int i = 0; i < 64; i++)
        {
            await store.CreateNodeAsync(new StoredGraphNode((ulong)storage.ReserveTransactionSequence(), ["Person"], Properties(i)),
                TestLabelIds.All, seed, timeout.Token);
        }
        await coordinator.CommitAsync(seed, timeout.Token);
        var old = await coordinator.BeginAsync(IsolationLevel.Snapshot, timeout.Token);
        // A fixed number of rounds, never a wall-clock window: each round is a scripted
        // interleaving, so the count alone decides coverage.
        const int rounds = 64;
        int continueRace = 1;
        int reading = 0, writing = 0, reads = 0, writes = 0, readOverlaps = 0, writeOverlaps = 0, rollbacks = 0, purges = 0;
        using var phases = new Barrier(2, phase =>
        {
            // Each round has cursor-open, mutation-complete and reader-complete phases.
            // Decide once for both tasks, after both completed the round.
            if (phase.CurrentPhaseNumber % 3 == 2)
            { Volatile.Write(ref continueRace, reads < rounds ? 1 : 0); }
        });
        store.OnLabelEntryRead = () =>
        {
            // Hold the captured posting cursor across a replacement and commit/rollback plus purge.
            // The callback runs after the B+Tree has released its latch.
            phases.SignalAndWait(timeout.Token);
            if (Volatile.Read(ref writing) != 0) { Interlocked.Increment(ref readOverlaps); }
            phases.SignalAndWait(timeout.Token);
        };

        // Act: exactly `rounds` coordinated races.
        var reader = DedicatedTestTask.Run(() =>
        {
            for (int iteration = 0; iteration < rounds; iteration++)
            {
                timeout.Token.ThrowIfCancellationRequested();
                Volatile.Write(ref reading, 1);
                try
                {
                    var found = store.GetNodes("N", old.Snapshot).ShouldHaveSingleItem();
                    found.Id.ShouldBe(node.Id);
                    found.Properties["x"].ShouldBe(0);
                    store.GetNodes("M", old.Snapshot).ShouldBeEmpty();
                    Interlocked.Increment(ref reads);
                }
                finally { Volatile.Write(ref reading, 0); }
                phases.SignalAndWait(timeout.Token);
                if (Volatile.Read(ref continueRace) == 0) { break; }
            }
        }, timeout.Token);
        var writer = DedicatedTestTask.Run(async () =>
        {
            for (int iteration = 0; iteration < rounds; iteration++)
            {
                timeout.Token.ThrowIfCancellationRequested();
                Volatile.Write(ref writing, 1);
                phases.SignalAndWait(timeout.Token);
                var context = await coordinator.BeginAsync(IsolationLevel.Snapshot, timeout.Token);
                try
                {
                    int version = iteration + 1;
                    await store.UpdateNodeAsync(node.Id, [version % 2 == 0 ? "N" : "M"], Properties(version), TestLabelIds.All, context, timeout.Token);
                    if (Volatile.Read(ref reading) != 0) { Interlocked.Increment(ref writeOverlaps); }
                    if (version % 3 == 0)
                    { await coordinator.RollbackAsync(context, timeout.Token); Interlocked.Increment(ref rollbacks); }
                    else { await coordinator.CommitAsync(context, timeout.Token); }
                    coordinator.RunVersionPurgePass(timeout.Token);
                    Interlocked.Increment(ref purges);
                    Interlocked.Increment(ref writes);
                    phases.SignalAndWait(timeout.Token);
                }
                finally
                {
                    if (context.State == TransactionState.Active) { await coordinator.RollbackAsync(context, CancellationToken.None); }
                    Volatile.Write(ref writing, 0);
                }
                phases.SignalAndWait(timeout.Token);
                if (Volatile.Read(ref continueRace) == 0) { break; }
            }
        }, timeout.Token);
        try
        {
            await Task.WhenAll(reader, writer);

            // Assert: both tasks stopped; this store has no maintenance workers, so only this
            // selected-label read can increment the decode counter. Noise must not be decoded.
            store.OnLabelEntryRead = null;
            long before = store.NodeRecordsDecoded;
            store.GetNodes("N", old.Snapshot).ShouldHaveSingleItem().Id.ShouldBe(node.Id);
            (store.NodeRecordsDecoded - before).ShouldBeInRange(1L, 1L);
            store.LabelEntriesVisited.ShouldBeGreaterThan(0);
        }
        finally
        {
            store.OnLabelEntryRead = null;
            await coordinator.RollbackAsync(old, CancellationToken.None);
        }

        // Assert
        reads.ShouldBe(rounds);
        writes.ShouldBe(reads);
        readOverlaps.ShouldBe(reads);
        writeOverlaps.ShouldBe(writes);
        rollbacks.ShouldBeGreaterThan(0);
        purges.ShouldBe(writes);
        coordinator.RunVersionPurgePass(timeout.Token).ShouldBeGreaterThan(0);
    }

    private static IReadOnlyDictionary<string, object?> Properties(int value) => new Dictionary<string, object?> { ["x"] = value };
}
