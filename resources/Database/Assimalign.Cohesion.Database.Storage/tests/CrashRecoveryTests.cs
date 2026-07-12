using System;
using System.IO;
using System.Linq;
using System.Text;
using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Storage.Tests;

/// <summary>
/// Crash, restart, and recovery suites proving the durability guarantees (#160/#162):
/// committed work survives a crash without any data-page flush (redo), uncommitted
/// work never survives even when stolen page writes reached the data file (undo),
/// and recovery is idempotent.
/// </summary>
public sealed class CrashRecoveryTests
{
    /// <summary>
    /// A concrete storage exposing record operations for the crash harness. The data
    /// stream is write-through (worst-case steal: the OS persists every page write
    /// immediately); the journal honors flush-gated durability so unforced appends
    /// die with the process.
    /// </summary>
    private sealed class HarnessStorage : Storage
    {
        private HarnessStorage(StorageStream data, StorageStream journal, int poolCapacity)
            : base(data, journal, new StorageStream(new MemoryStream()), poolCapacity)
        {
        }

        public override StorageModel Model => StorageModel.Custom;

        public static HarnessStorage Create(CrashSimulationStream data, CrashSimulationStream journal, int poolCapacity = 8)
        {
            var storage = new HarnessStorage(new StorageStream(data), new StorageStream(journal), poolCapacity);
            storage.InitializeNew((Name)"crash-harness");
            return storage;
        }

        public static HarnessStorage Open(byte[] data, byte[] journal, int poolCapacity = 8)
        {
            var storage = new HarnessStorage(
                new StorageStream(new CrashSimulationStream(data, writeThrough: true)),
                new StorageStream(new CrashSimulationStream(journal)),
                poolCapacity);
            storage.OpenExisting();
            return storage;
        }

        public (PageId PageId, int SlotIndex) Insert(IStorageTransaction transaction, byte[] data)
            => InsertRecord(transaction, data);

        public (PageId PageId, int SlotIndex) Insert(byte[] data) => InsertRecord(data);

        public void Update(IStorageTransaction transaction, PageId pageId, int slotIndex, byte[] data)
            => UpdateRecord(transaction, pageId, slotIndex, data);

        public ReadOnlyMemory<byte> Read(PageId pageId, int slotIndex) => ReadRecord(pageId, slotIndex);

        public string[] ScanText()
        {
            var results = new System.Collections.Generic.List<string>();
            using var iterator = GetUnitIterator();
            while (iterator.MoveNext())
            {
                results.Add(Encoding.UTF8.GetString(iterator.Current.Data.Span));
            }
            return results.ToArray();
        }
    }

    private static byte[] Text(string value) => Encoding.UTF8.GetBytes(value);

    [Fact(DisplayName = "Cohesion Test [Storage] - Recovery: committed work survives a crash with no page flush (redo)")]
    public void Recovery_CommittedTransactionWithoutPageFlush_ShouldBeRedone()
    {
        // Arrange: commit a transaction and crash immediately — data pages were
        // never flushed; only the journal is durable.
        var data = new CrashSimulationStream(writeThrough: false);
        var journal = new CrashSimulationStream(writeThrough: false);
        var storage = HarnessStorage.Create(data, journal);

        byte[] durableDataAtInit = data.CaptureDurable();

        using (var transaction = storage.BeginTransaction())
        {
            storage.Insert(transaction, Text("committed-row"));
            transaction.Commit();
        }

        // Crash: take only what is durable. The data stream still holds just the
        // initial flush; the journal holds the commit.
        byte[] crashedData = data.CaptureDurable();
        byte[] crashedJournal = journal.CaptureDurable();
        crashedData.ShouldBe(durableDataAtInit); // proves no data page flush happened

        // Act: reopen from the crashed state.
        using var recovered = HarnessStorage.Open(crashedData, crashedJournal);

        // Assert
        recovered.ScanText().ShouldBe(new[] { "committed-row" });
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Recovery: a transaction without a durable commit record leaves no effects")]
    public void Recovery_CrashBeforeCommitRecord_ShouldLeaveNoPartialEffects()
    {
        // Arrange: mutate inside a transaction but crash before Commit().
        var data = new CrashSimulationStream(writeThrough: true); // worst case: stolen writes persist
        var journal = new CrashSimulationStream(writeThrough: false);
        var storage = HarnessStorage.Create(data, journal);

        var transaction = storage.BeginTransaction();
        storage.Insert(transaction, Text("uncommitted-row"));

        // Force the dirty page to disk mid-transaction (steal) via an explicit flush.
        storage.PageManager.FlushAll();

        // Crash without committing.
        byte[] crashedData = data.CaptureDurable();
        byte[] crashedJournal = journal.CaptureDurable();

        // Act
        using var recovered = HarnessStorage.Open(crashedData, crashedJournal);

        // Assert: the stolen write was undone from the before image.
        recovered.ScanText().ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Recovery: stolen uncommitted update is undone to the pre-transaction value")]
    public void Recovery_StolenUncommittedUpdate_ShouldRestoreCommittedValue()
    {
        // Arrange: commit a row, then update it in a second transaction, steal the
        // page to disk, and crash before the second commit.
        var data = new CrashSimulationStream(writeThrough: true);
        var journal = new CrashSimulationStream(writeThrough: false);
        var storage = HarnessStorage.Create(data, journal);

        PageId pageId;
        int slotIndex;

        using (var setup = storage.BeginTransaction())
        {
            (pageId, slotIndex) = storage.Insert(setup, Text("original"));
            setup.Commit();
        }

        var update = storage.BeginTransaction();
        storage.Update(update, pageId, slotIndex, Text("mutated!"));
        storage.PageManager.FlushAll(); // steal the dirty page to disk

        byte[] crashedData = data.CaptureDurable();
        byte[] crashedJournal = journal.CaptureDurable();

        // Act
        using var recovered = HarnessStorage.Open(crashedData, crashedJournal);

        // Assert
        Encoding.UTF8.GetString(recovered.Read(pageId, slotIndex).Span).ShouldBe("original");
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Recovery: replay is idempotent across repeated crashes")]
    public void Recovery_RepeatedReplay_ShouldBeIdempotent()
    {
        // Arrange
        var data = new CrashSimulationStream(writeThrough: false);
        var journal = new CrashSimulationStream(writeThrough: false);
        var storage = HarnessStorage.Create(data, journal);

        using (var transaction = storage.BeginTransaction())
        {
            storage.Insert(transaction, Text("row-a"));
            storage.Insert(transaction, Text("row-b"));
            transaction.Commit();
        }

        byte[] crashedData = data.CaptureDurable();
        byte[] crashedJournal = journal.CaptureDurable();

        // Act: recover once, crash again immediately (before its checkpoint flushed
        // anything new), recover again from the same inputs.
        using (var firstRecovery = HarnessStorage.Open(crashedData, crashedJournal))
        {
            firstRecovery.ScanText().ShouldBe(new[] { "row-a", "row-b" });
        }

        using var secondRecovery = HarnessStorage.Open(crashedData, crashedJournal);

        // Assert
        secondRecovery.ScanText().ShouldBe(new[] { "row-a", "row-b" });
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Recovery: rollback before crash leaves no effects")]
    public void Recovery_RolledBackTransaction_ShouldLeaveNoEffects()
    {
        // Arrange
        var data = new CrashSimulationStream(writeThrough: true);
        var journal = new CrashSimulationStream(writeThrough: false);
        var storage = HarnessStorage.Create(data, journal);

        using (var setup = storage.BeginTransaction())
        {
            storage.Insert(setup, Text("kept"));
            setup.Commit();
        }

        using (var abandoned = storage.BeginTransaction())
        {
            storage.Insert(abandoned, Text("discarded"));
            abandoned.Rollback();
        }

        // In-memory state already reflects the rollback.
        storage.ScanText().ShouldBe(new[] { "kept" });

        byte[] crashedData = data.CaptureDurable();
        byte[] crashedJournal = journal.CaptureDurable();

        // Act
        using var recovered = HarnessStorage.Open(crashedData, crashedJournal);

        // Assert
        recovered.ScanText().ShouldBe(new[] { "kept" });
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Recovery: clean shutdown checkpoints and reopens with an empty journal")]
    public void Recovery_CleanShutdown_ShouldCheckpointAndReopenFast()
    {
        // Arrange
        var data = new CrashSimulationStream(writeThrough: false);
        var journal = new CrashSimulationStream(writeThrough: false);
        var storage = HarnessStorage.Create(data, journal);

        using (var transaction = storage.BeginTransaction())
        {
            storage.Insert(transaction, Text("persisted"));
            transaction.Commit();
        }

        storage.Dispose(); // clean shutdown: checkpoint (flush + truncate)

        byte[] shutdownData = data.CaptureDurable();
        byte[] shutdownJournal = journal.CaptureDurable();

        // Act
        using var reopened = HarnessStorage.Open(shutdownData, shutdownJournal);

        // Assert: everything is in the data file; the journal carries only a checkpoint.
        reopened.ScanText().ShouldBe(new[] { "persisted" });
        using var journalReader = new StreamJournal(new MemoryStream(shutdownJournal), leaveOpen: false);
        var records = journalReader.ReadAll();
        records.Count.ShouldBe(1);
        records[0].Type.ShouldBe(JournalRecordType.Checkpoint);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Recovery: multi-transaction interleaving recovers only committed work")]
    public void Recovery_InterleavedTransactions_ShouldRecoverOnlyCommittedWork()
    {
        // Arrange: two transactions on separate pages; one commits, one does not.
        var data = new CrashSimulationStream(writeThrough: true);
        var journal = new CrashSimulationStream(writeThrough: false);
        var storage = HarnessStorage.Create(data, journal, poolCapacity: 16);

        // Fill page 1 nearly full so the second transaction allocates page 2.
        using (var setup = storage.BeginTransaction())
        {
            storage.Insert(setup, new byte[7000]);
            setup.Commit();
        }

        var committed = storage.BeginTransaction();
        var uncommitted = storage.BeginTransaction();

        storage.Insert(committed, Text("wins"));       // allocates page 2
        committed.Commit();

        storage.Insert(uncommitted, Text("loses"));    // allocates page 3
        storage.PageManager.FlushAll();                // steal everything

        byte[] crashedData = data.CaptureDurable();
        byte[] crashedJournal = journal.CaptureDurable();

        // Act
        using var recovered = HarnessStorage.Open(crashedData, crashedJournal);

        // Assert
        var rows = recovered.ScanText();
        rows.ShouldContain("wins");
        rows.ShouldNotContain("loses");
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Transactions: page write conflict between active transactions throws")]
    public void Transaction_PageWriteConflict_ShouldThrow()
    {
        // Arrange: two transactions targeting the same (current write) page.
        var data = new CrashSimulationStream(writeThrough: false);
        var journal = new CrashSimulationStream(writeThrough: false);
        using var storage = HarnessStorage.Create(data, journal);

        var first = storage.BeginTransaction();
        var second = storage.BeginTransaction();

        storage.Insert(first, Text("first"));

        // Act / Assert: the second transaction hits the page lock held by the first.
        Should.Throw<StorageTransactionException>(() => storage.Insert(second, Text("second")));

        first.Commit();

        // After the first commits, the page is free for the second transaction.
        storage.Insert(second, Text("second"));
        second.Commit();

        storage.ScanText().ShouldBe(new[] { "first", "second" });
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Transactions: completed transactions reject further use")]
    public void Transaction_CompletedTransaction_ShouldRejectFurtherUse()
    {
        // Arrange
        var data = new CrashSimulationStream(writeThrough: false);
        var journal = new CrashSimulationStream(writeThrough: false);
        using var storage = HarnessStorage.Create(data, journal);

        var transaction = storage.BeginTransaction();
        storage.Insert(transaction, Text("row"));
        transaction.Commit();

        // Act / Assert
        Should.Throw<StorageTransactionException>(() => transaction.Commit());
        Should.Throw<StorageTransactionException>(() => storage.Insert(transaction, Text("late")));
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Checkpoint: active transactions block checkpointing")]
    public void Checkpoint_WithActiveTransaction_ShouldThrow()
    {
        // Arrange
        var data = new CrashSimulationStream(writeThrough: false);
        var journal = new CrashSimulationStream(writeThrough: false);
        using var storage = HarnessStorage.Create(data, journal);

        var transaction = storage.BeginTransaction();
        storage.Insert(transaction, Text("open"));

        // Act / Assert
        Should.Throw<StorageTransactionException>(() => storage.Checkpoint());

        transaction.Rollback();
        storage.Checkpoint(); // idle now — succeeds
    }
}
