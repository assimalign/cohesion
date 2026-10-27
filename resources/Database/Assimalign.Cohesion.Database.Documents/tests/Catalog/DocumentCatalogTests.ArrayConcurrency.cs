using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents.Catalog.Tests;

public sealed partial class DocumentCatalogTests
{
    [Fact(DisplayName = "Cohesion Test [Documents.Catalog] - Array replacement, rollback, search cancellation and purge race with pinned snapshots (#1530)")]
    public async Task SearchIndexAsync_ArrayWritersAndPurge_ShouldPreservePinnedPostingsAndReleaseCursors()
    {
        // Arrange: this class is in the non-parallel Document index concurrency collection.
        await using var f = await Fixture.Create();
        var metadata = await CreateTypedIndexAsync(f, "ix", [DocumentIndexKey.DistinctArray("items")]);
        await f.Write("a", "{\"items\":[1,1,2]}");
        var pinned = await f.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        int writes = 0, reads = 0, rollbacks = 0, cancellations = 0, passes = 0;
        long purged = 0;
        var elapsed = Stopwatch.StartNew();

        async Task LoopAsync(Func<Task> iteration)
        {
            try
            {
                while (!stop.IsCancellationRequested) { await iteration(); await Task.Yield(); }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }

        var writerTask = DedicatedTestTask.Run(() => LoopAsync(async () =>
        {
            var writer = await f.Coordinator.BeginAsync(IsolationLevel.Snapshot, stop.Token);
            try
            {
                await f.WriteIn(writer, "a", "{\"items\":[2,3,3,4]}");
                if ((Volatile.Read(ref writes) & 1) == 0)
                { await f.Coordinator.RollbackAsync(writer); Interlocked.Increment(ref rollbacks); }
                else { await f.Coordinator.CommitAsync(writer); }
                Interlocked.Increment(ref writes);
            }
            finally { if (writer.State == TransactionState.Active) { await f.Coordinator.RollbackAsync(writer); } }
        }), CancellationToken.None);
        var readerTask = DedicatedTestTask.Run(() => LoopAsync(async () =>
        {
            var reader = await f.Coordinator.BeginAsync(IsolationLevel.Snapshot, stop.Token);
            try
            {
                (await f.Catalog.SearchIndexAsync(f.Collection.Id, "ix", null, true, null, true, reader.Snapshot, stop.Token))
                    .Select(document => document.Id).ShouldBe(["a"]);
                using var canceled = new CancellationTokenSource();
                canceled.Cancel();
                await Should.ThrowAsync<OperationCanceledException>(async () =>
                    await f.Catalog.SearchIndexAsync(f.Collection.Id, "ix", null, true, null, true, reader.Snapshot, canceled.Token));
                Interlocked.Increment(ref cancellations);
                Interlocked.Increment(ref reads);
            }
            finally { await f.Coordinator.RollbackAsync(reader); }
        }), CancellationToken.None);
        var purgeTask = DedicatedTestTask.Run(() => LoopAsync(() =>
        {
            Interlocked.Add(ref purged, f.Catalog.RunVersionPurgePass(stop.Token));
            Interlocked.Increment(ref passes);
            return Task.CompletedTask;
        }), CancellationToken.None);
        var all = Task.WhenAll(writerTask, readerTask, purgeTask);
        int releasedWrites = 0, releasedReads = 0, releasedPasses = 0;
        long releasedIndexPasses = 0;

        // Act: one minimum second plus observed progress, then require more work after unpinning.
        try
        {
            while ((elapsed.Elapsed < TimeSpan.FromSeconds(1) || Volatile.Read(ref writes) < 32 ||
                Volatile.Read(ref reads) < 32 || Volatile.Read(ref passes) < 4) && !all.IsCompleted && !stop.IsCancellationRequested)
            {
                (await f.Catalog.SearchIndexAsync(f.Collection.Id, "ix", 1m, true, 1m, true, pinned.Snapshot, stop.Token))
                    .Single().Id.ShouldBe("a");
                (await ReadRawKeysAsync(f, metadata, pinned.Snapshot, stop.Token)).Count.ShouldBe(2);
                await Task.Delay(5, CancellationToken.None);
            }
            await f.Coordinator.RollbackAsync(pinned);
            releasedWrites = Volatile.Read(ref writes);
            releasedReads = Volatile.Read(ref reads);
            releasedPasses = Volatile.Read(ref passes);
            releasedIndexPasses = f.IndexPrunePasses;
            while ((Volatile.Read(ref writes) - releasedWrites < 32 || Volatile.Read(ref reads) - releasedReads < 32 ||
                Volatile.Read(ref passes) - releasedPasses < 4 || f.IndexPostingsPruned == 0) &&
                !all.IsCompleted && !stop.IsCancellationRequested)
            { await Task.Delay(5, CancellationToken.None); }
        }
        finally
        {
            stop.Cancel();
            await all.WaitAsync(TimeSpan.FromSeconds(30));
            if (pinned.State == TransactionState.Active) { await f.Coordinator.RollbackAsync(pinned); }
        }

        // Assert: measured counters, never elapsed time, decide whether the race was exercised.
        writes.ShouldBeGreaterThanOrEqualTo(32);
        reads.ShouldBeGreaterThanOrEqualTo(32);
        rollbacks.ShouldBeGreaterThan(0);
        cancellations.ShouldBe(reads);
        passes.ShouldBeGreaterThanOrEqualTo(4);
        purged.ShouldBeGreaterThan(0);
        releasedIndexPasses.ShouldBeGreaterThanOrEqualTo(4);
        (f.IndexPrunePasses - releasedIndexPasses).ShouldBeGreaterThanOrEqualTo(4);
        f.IndexPostingsPruned.ShouldBeGreaterThan(0);
        (writes - releasedWrites).ShouldBeGreaterThanOrEqualTo(32);
        (reads - releasedReads).ShouldBeGreaterThanOrEqualTo(32);
        (passes - releasedPasses).ShouldBeGreaterThanOrEqualTo(4);
        // A final committed delete plus purge must physically erase the first version,
        // even at the old horizon that cannot see any deleting transaction.
        var deleting = await f.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        await f.Catalog.DeleteDocumentAsync(f.Collection.Id, "a", deleting);
        await f.Coordinator.CommitAsync(deleting);
        for (int i = 0; i < 16; i++) { f.Coordinator.RunVersionPurgePass(CancellationToken.None); }
        (await ReadRawKeysAsync(f, metadata, pinned.Snapshot)).ShouldBeEmpty();
    }
}
