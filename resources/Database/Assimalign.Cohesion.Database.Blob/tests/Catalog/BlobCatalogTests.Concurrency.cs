using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Blob.Catalog.Tests;

public sealed partial class BlobCatalogTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Blob.Catalog] - Concurrent overwrites, rollback, snapshots and purge all make progress (#1281)")]
    public async Task FindBlob_ConcurrentOverwriteRollbackAndPurge_ShouldPreserveSnapshotsAndProgress()
    {
        // Arrange: the containing collection disables test parallelization.
        await using var database = new TestDatabase();
        var coordinator = database.Coordinator;
        var catalog = database.Catalog;
        var parent = Guid.NewGuid();
        var original = Entry(parent, "item");
        var seed = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        await catalog.SaveBlobAsync(original, seed);
        await coordinator.CommitAsync(seed);
        var pinned = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var expected = original;
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        int writes = 0;
        int reads = 0;
        int rollbacks = 0;
        int passes = 0;
        long purged = 0;
        int releasedWrites = 0;
        int releasedReads = 0;
        int releasedPasses = 0;

        async Task Run(Func<Task> iteration)
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    await iteration();
                    await Task.Yield();
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }

        var writerTask = Task.Run(() => Run(async () =>
        {
            var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot, stop.Token);
            try
            {
                await catalog.SaveBlobAsync(original with { ETag = (ulong)Volatile.Read(ref writes) + 100 }, writer, stop.Token);
                if (Volatile.Read(ref writes) % 4 == 0)
                {
                    await coordinator.RollbackAsync(writer);
                    Interlocked.Increment(ref rollbacks);
                }
                else { await coordinator.CommitAsync(writer); }
                Interlocked.Increment(ref writes);
            }
            finally
            {
                if (writer.State == TransactionState.Active) { await coordinator.RollbackAsync(writer); }
            }
        }), CancellationToken.None);
        var readerTask = Task.Run(() => Run(async () =>
        {
            var reader = await coordinator.BeginAsync(IsolationLevel.Snapshot, stop.Token);
            try
            {
                catalog.FindBlob(parent, "item", reader.Snapshot).ShouldNotBeNull();
                catalog.GetBlobs(parent, null, reader.Snapshot).ShouldHaveSingleItem();
                Interlocked.Increment(ref reads);
            }
            finally { await coordinator.RollbackAsync(reader); }
        }), CancellationToken.None);
        var purgeTask = Task.Run(() => Run(() =>
        {
            Interlocked.Add(ref purged, catalog.RunVersionPurgePass(stop.Token));
            Interlocked.Increment(ref passes);
            return Task.CompletedTask;
        }), CancellationToken.None);
        var all = Task.WhenAll(writerTask, readerTask, purgeTask);

        // Act: retain an old snapshot first, then release it while the same tasks keep racing.
        try
        {
            while ((Volatile.Read(ref writes) < 64
                || Volatile.Read(ref reads) < 64 || Volatile.Read(ref passes) < 4)
                && !all.IsCompleted && !stop.IsCancellationRequested)
            {
                catalog.FindBlob(parent, "item", pinned.Snapshot).ShouldBe(expected);
                await Task.Delay(5, CancellationToken.None);
            }
            catalog.FindBlob(parent, "item", pinned.Snapshot).ShouldBe(expected);
            await coordinator.RollbackAsync(pinned);
            releasedWrites = Volatile.Read(ref writes);
            releasedReads = Volatile.Read(ref reads);
            releasedPasses = Volatile.Read(ref passes);
            while ((Volatile.Read(ref writes) - releasedWrites < 64 || Volatile.Read(ref reads) - releasedReads < 64
                || Volatile.Read(ref passes) - releasedPasses < 4 || Interlocked.Read(ref purged) == 0)
                && !all.IsCompleted && !stop.IsCancellationRequested)
            {
                await Task.Delay(5, CancellationToken.None);
            }
        }
        finally
        {
            stop.Cancel();
            await all;
            if (pinned.State == TransactionState.Active) { await coordinator.RollbackAsync(pinned); }
        }

        // Assert: counters prove overwrite, rollback, read and physical reclamation overlapped.
        writes.ShouldBeGreaterThanOrEqualTo(64);
        reads.ShouldBeGreaterThanOrEqualTo(64);
        rollbacks.ShouldBeGreaterThan(0);
        passes.ShouldBeGreaterThanOrEqualTo(4);
        purged.ShouldBeGreaterThan(0);
        (writes - releasedWrites).ShouldBeGreaterThanOrEqualTo(64);
        (reads - releasedReads).ShouldBeGreaterThanOrEqualTo(64);
        (passes - releasedPasses).ShouldBeGreaterThanOrEqualTo(4);
        catalog.RunVersionPurgePass(default);
        var fresh = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        int retained = catalog.CachedReferenceCount;
        for (int probe = 0; probe < retained; probe++) { catalog.FindBlob(parent, "item", fresh.Snapshot).ShouldNotBeNull(); }
        catalog.CachedReferenceCount.ShouldBe(1);
        await coordinator.RollbackAsync(fresh);
    }
}
