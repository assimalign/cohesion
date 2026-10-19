using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

namespace Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

/// <summary>
/// The grouped-commit scenario <see cref="StorageWorkerSupportTests"/> runs as it is and
/// <see cref="StorageGroupCommitThreadPoolTests"/> runs with the thread pool held.
/// </summary>
internal static class GroupedCommitScenario
{
    /// <summary>
    /// A grouped commit registers on the gate, is not acknowledged before the flush worker's pass,
    /// and completes, durable, once the pass lands.
    /// </summary>
    /// <remarks>
    /// Nothing here waits for the thread pool: the committer runs on a thread of its own, and every
    /// wait blocks the test's thread. Test classes run in parallel on pool threads and fill the pool
    /// with busy loops (the concurrency tests' writers, readers and write-back passes), and a
    /// committer started with <c>Task.Run</c> from a test's own pool thread lands in that thread's
    /// local queue, which another worker steals from only once the global queue is empty. It waited
    /// there for seconds on 4 cores, while the commit itself registers within 2 ms of starting; that
    /// is the likely cause of the 10 s registration timeout CI showed at 64c4b831.
    /// </remarks>
    public static void AssertCompletesOnWorkerFlush()
    {
        // Arrange: a window long enough that self-help cannot kick in during the
        // test, so completion is attributable to the worker's flush alone.
        using var storage = WorkerStorage.Create(new MemoryStream(), new MemoryStream());
        storage.CommitDurability = StorageCommitDurability.Grouped;
        storage.GroupCommitWindow = TimeSpan.FromSeconds(20);

        using var pending = new ManualResetEventSlim();
        storage.OnCommitPending = pending.Set;

        // Act: commit on a thread of its own; it registers on the gate and waits.
        Task commit = Task.Factory.StartNew(() =>
        {
            using var transaction = storage.BeginTransaction();
            storage.Insert(transaction, new byte[] { 4, 5, 6 });
            transaction.Commit();
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        // A commit that fails, or returns without registering, surfaces as itself, not as a timeout.
        WaitHandle.WaitAny([pending.WaitHandle, ((IAsyncResult)commit).AsyncWaitHandle], TimeSpan.FromSeconds(10));
        if (!pending.IsSet && commit.IsCompleted)
        {
            commit.Wait();
            throw new ShouldAssertException("The grouped commit returned without registering on the gate.");
        }

        pending.IsSet.ShouldBeTrue("the grouped commit did not register on the gate within 10 s");

        // The commit is registered but not acknowledged: nothing has flushed yet.
        commit.Wait(TimeSpan.FromMilliseconds(200)).ShouldBeFalse();

        // The "flush worker" performs one group flush pass.
        storage.FlushPendingCommits().ShouldBeTrue();

        // Assert: the flush released the committer, and its records are durable.
        commit.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue();
        storage.Wal.DurableLsn.ShouldBe(storage.Wal.LastLsn);
    }
}
