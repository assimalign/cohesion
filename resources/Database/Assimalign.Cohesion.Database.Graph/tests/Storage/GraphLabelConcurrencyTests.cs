using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Transactions;

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
        await coordinator.CommitAsync(seed, timeout.Token);
        var old = await coordinator.BeginAsync(IsolationLevel.Snapshot, timeout.Token);
        using var start = new Barrier(2);
        var elapsed = Stopwatch.StartNew();
        int reading = 0, writing = 0, reads = 0, writes = 0, readOverlaps = 0, writeOverlaps = 0, rollbacks = 0, purges = 0;
        store.OnLabelEntryRead = () =>
        {
            if (Volatile.Read(ref writing) != 0) { Interlocked.Increment(ref readOverlaps); }
        };

        // Act: continue after the minimum duration until the counters prove both overlaps.
        var reader = Task.Run(async () =>
        {
            start.SignalAndWait(timeout.Token);
            do
            {
                timeout.Token.ThrowIfCancellationRequested();
                Volatile.Write(ref reading, 1);
                try
                {
                    await Task.Yield();
                    var found = store.GetNodes("N", old.Snapshot).ShouldHaveSingleItem();
                    found.Id.ShouldBe(node.Id);
                    found.Properties["x"].ShouldBe(0);
                    store.GetNodes("M", old.Snapshot).ShouldBeEmpty();
                    Interlocked.Increment(ref reads);
                }
                finally { Volatile.Write(ref reading, 0); }
                await Task.Yield();
            } while (Continue());
        }, timeout.Token);
        var writer = Task.Run(async () =>
        {
            start.SignalAndWait(timeout.Token);
            int version = 0;
            do
            {
                timeout.Token.ThrowIfCancellationRequested();
                Volatile.Write(ref writing, 1);
                var context = await coordinator.BeginAsync(IsolationLevel.Snapshot, timeout.Token);
                try
                {
                    await store.UpdateNodeAsync(node.Id, [++version % 2 == 0 ? "N" : "M"], Properties(version), TestLabelIds.All, context, timeout.Token);
                    await Task.Yield();
                    if (Volatile.Read(ref reading) != 0) { Interlocked.Increment(ref writeOverlaps); }
                    if (version % 3 == 0)
                    { await coordinator.RollbackAsync(context, timeout.Token); Interlocked.Increment(ref rollbacks); }
                    else { await coordinator.CommitAsync(context, timeout.Token); }
                    coordinator.RunVersionPurgePass(timeout.Token);
                    Interlocked.Increment(ref purges);
                    Interlocked.Increment(ref writes);
                }
                finally
                {
                    if (context.State == TransactionState.Active) { await coordinator.RollbackAsync(context, CancellationToken.None); }
                    Volatile.Write(ref writing, 0);
                }
                await Task.Yield();
            } while (Continue());
        }, timeout.Token);
        try { await Task.WhenAll(reader, writer); }
        finally
        {
            store.OnLabelEntryRead = null;
            await coordinator.RollbackAsync(old, CancellationToken.None);
        }

        // Assert
        reads.ShouldBeGreaterThanOrEqualTo(8);
        writes.ShouldBeGreaterThanOrEqualTo(8);
        readOverlaps.ShouldBeGreaterThan(0);
        writeOverlaps.ShouldBeGreaterThan(0);
        rollbacks.ShouldBeGreaterThan(0);
        purges.ShouldBe(writes);
        coordinator.RunVersionPurgePass(timeout.Token).ShouldBeGreaterThan(0);

        bool Continue() => elapsed.Elapsed < TimeSpan.FromMilliseconds(250) || Volatile.Read(ref reads) < 8 ||
            Volatile.Read(ref writes) < 8 || Volatile.Read(ref readOverlaps) == 0 || Volatile.Read(ref writeOverlaps) == 0;
    }

    private static IReadOnlyDictionary<string, object?> Properties(int value) => new Dictionary<string, object?> { ["x"] = value };
}
