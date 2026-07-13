using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Storage.Tests;

/// <summary>
/// Tests for the storage seams the engine-owned background workers drive (#902):
/// grouped commit durability (worker flush + self-help), paced dirty-page write-back
/// under the write-ahead gate, and the checkpoint/active-transaction interlock.
/// </summary>
public sealed class StorageWorkerSupportTests
{
    /// <summary>
    /// Minimal concrete storage exposing the journal for durability assertions.
    /// </summary>
    private sealed class WorkerStorage : Storage
    {
        private WorkerStorage(StorageStream data, StorageStream journal)
            : base(data, journal, new StorageStream(new MemoryStream())) { }

        public override StorageModel Model => StorageModel.Sql;

        public IStorageJournal Wal => WriteAheadLog;

        public static WorkerStorage Create(Stream data, Stream journal)
        {
            var storage = new WorkerStorage(new StorageStream(data), new StorageStream(journal));
            storage.InitializeNew((Name)"worker-support");
            return storage;
        }

        public (PageId PageId, int SlotIndex) Insert(IStorageTransaction transaction, ReadOnlySpan<byte> data)
            => InsertRecord(transaction, data);

        public (PageId PageId, int SlotIndex) Insert(ReadOnlySpan<byte> data)
            => InsertRecord(data);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - GroupCommit: A grouped commit without a worker self-helps to durability")]
    public void Commit_GroupedWithoutWorker_ShouldSelfHelpToDurability()
    {
        // Arrange: grouped durability, a short window, and no flush worker at all.
        var journalStream = new CrashSimulationStream();
        using var storage = WorkerStorage.Create(new MemoryStream(), journalStream);
        storage.CommitDurability = StorageCommitDurability.Grouped;
        storage.GroupCommitWindow = TimeSpan.FromMilliseconds(50);

        // Act: the commit must complete on its own (self-help flush) and only after
        // its records are durable.
        using (var transaction = storage.BeginTransaction())
        {
            storage.Insert(transaction, new byte[] { 1, 2, 3 });
            transaction.Commit();
        }

        // Assert: everything appended (including the commit record) is durable, and
        // the journal medium saw a durable flush.
        storage.Wal.DurableLsn.ShouldBe(storage.Wal.LastLsn);
        journalStream.CaptureDurable().Length.ShouldBeGreaterThan(0);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - GroupCommit: A grouped commit waits for the worker's flush and completes when it lands")]
    public async Task Commit_GroupedWithWorker_ShouldCompleteWhenWorkerFlushes()
    {
        // Arrange: a window long enough that self-help cannot kick in during the
        // test, so completion is attributable to the worker's flush alone.
        using var storage = WorkerStorage.Create(new MemoryStream(), new MemoryStream());
        storage.CommitDurability = StorageCommitDurability.Grouped;
        storage.GroupCommitWindow = TimeSpan.FromSeconds(20);

        using var pending = new ManualResetEventSlim();
        storage.OnCommitPending = pending.Set;

        // Act: commit on a background thread; it registers on the gate and waits.
        Task commit = Task.Run(() =>
        {
            using var transaction = storage.BeginTransaction();
            storage.Insert(transaction, new byte[] { 4, 5, 6 });
            transaction.Commit();
        });

        pending.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue();

        // The commit is registered but not acknowledged: nothing has flushed yet.
        await Task.Delay(200);
        commit.IsCompleted.ShouldBeFalse();

        // The "flush worker" performs one group flush pass.
        storage.FlushPendingCommits().ShouldBeTrue();

        // Assert: the flush released the committer, and its records are durable.
        (await Task.WhenAny(commit, Task.Delay(TimeSpan.FromSeconds(10)))).ShouldBe(commit);
        await commit;
        storage.Wal.DurableLsn.ShouldBe(storage.Wal.LastLsn);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - GroupCommit: FlushPendingCommits is a no-op when nothing is pending")]
    public void FlushPendingCommits_WithNothingPending_ShouldReturnFalse()
    {
        // Arrange
        using var storage = WorkerStorage.Create(new MemoryStream(), new MemoryStream());

        // Act / Assert: synchronous mode never registers on the gate.
        storage.Insert(new byte[] { 7 });
        storage.FlushPendingCommits().ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - WriteBack: Paced write-back forces the journal durable past the page LSN first")]
    public void WriteBackDirtyPages_WithStolenWrite_ShouldForceJournalDurableFirst()
    {
        // Arrange: an open transaction has touched a page (before image appended but
        // not yet durably flushed — the journal medium is flush-gated).
        var journalStream = new CrashSimulationStream();
        using var storage = WorkerStorage.Create(new MemoryStream(), journalStream);

        using var transaction = storage.BeginTransaction();
        storage.Insert(transaction, new byte[] { 8, 9, 10 });

        journalStream.CaptureDurable().Length.ShouldBe(0);

        // Act: a page-writer pass writes the dirty page back mid-transaction (steal).
        int written = storage.WriteBackDirtyPages(maxPages: 16);

        // Assert: pages were written, and the write-ahead gate made the journal
        // durable up to the page's LSN (the before image) before any page reached
        // the data stream — the stolen write is always undoable.
        written.ShouldBeGreaterThan(0);
        journalStream.CaptureDurable().Length.ShouldBeGreaterThan(0);
        storage.Wal.DurableLsn.ShouldBe(storage.Wal.LastLsn);

        transaction.Rollback();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - WriteBack: The per-pass page batch is bounded")]
    public void WriteBackDirtyPages_WithManyDirtyPages_ShouldHonorTheBatchBound()
    {
        // Arrange: dirty several pages (4000-byte records force page turnover).
        using var storage = WorkerStorage.Create(new MemoryStream(), new MemoryStream());
        var payload = new byte[4000];

        using (var transaction = storage.BeginTransaction())
        {
            for (int i = 0; i < 6; i++)
            {
                storage.Insert(transaction, payload);
            }

            transaction.Commit();
        }

        // Act / Assert: one pass writes exactly the requested bound.
        storage.WriteBackDirtyPages(maxPages: 1).ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Checkpoint: A begun-but-untouched transaction blocks the checkpoint")]
    public void Checkpoint_WithBegunUntouchedTransaction_ShouldThrow()
    {
        // Arrange: the transaction has appended its begin record but touched no page
        // yet — the page-lock table is empty, but truncating now would still orphan
        // the transaction's journal presence. The active-transaction count guards it.
        using var storage = WorkerStorage.Create(new MemoryStream(), new MemoryStream());
        var transaction = storage.BeginTransaction();

        // Act / Assert
        Should.Throw<StorageTransactionException>(() => storage.Checkpoint());

        transaction.Commit();
        Should.NotThrow(() => storage.Checkpoint());
    }
}
