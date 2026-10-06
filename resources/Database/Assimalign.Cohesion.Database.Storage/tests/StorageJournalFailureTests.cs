using System;
using System.IO;
using System.Linq;
using System.Text;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Storage.Tests;

/// <summary>
/// What a journal write failure leaves behind. Since #1252 an append writes nothing: it encodes
/// its frame in the journal's append buffer, and the buffer drains to the medium at a full
/// buffer, a commit, a reader, the write-ahead gate, a checkpoint and a close. A drain that fails
/// takes the journal and its storage offline (#1243's rule, PostgreSQL's <c>PANIC</c> on a failed
/// WAL write, <c>xlog.c:2529-2532</c>): the frames it carried are already described by pages in
/// the buffer pool, so neither cutting them off nor retrying them is safe. The reopen's recovery
/// reads what the medium holds.
/// </summary>
/// <remarks>
/// Until #1252 every append was its own write, and a failed one was recoverable: the partial
/// frame was cut back off and the bracket's begin, before-image and rollback failures each left
/// the storage usable (#1226). The bookkeeping those tests pinned — a bracket whose begin record
/// fails is not counted, a page whose before image fails is unlocked, a rollback whose record
/// fails still ends — is kept, and is checked here with the drains that can now fail inside those
/// appends: the journal's buffer is shrunk to one small frame, so every append drains what
/// precedes it.
/// </remarks>
public sealed class StorageJournalFailureTests
{
    /// <summary>
    /// The drain's failure takes the storage offline through the same hook every other offline path
    /// raises (<see cref="Storage.OnOffline"/>, which the engines wire to the coordinator's
    /// <c>AbandonLockWaits</c>, #1268), once, with <see cref="StorageOfflineCause.JournalFlush"/>,
    /// the cause a failed journal fsync reports too; the message names the write.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Storage] - Journal failure: a begin record whose drain fails takes the storage offline and the bracket is not counted")]
    public void BeginTransaction_DrainFails_ShouldGoOfflineWithoutCountingTheBracket()
    {
        // Arrange: a committed row, then a bracket whose begin record must drain the one before.
        using var storage = FailureStorage.Create(out var journal);
        var raised = new System.Collections.Generic.List<StorageOfflineException>();
        storage.OnOffline = raised.Add;
        storage.Insert("kept");
        storage.Log.AppendOperation(0, [1, 2, 3]);
        journal.FailWrites = 1;

        // Act
        var error = Should.Throw<StorageOfflineException>(() => storage.BeginTransaction());
        var images = storage.CaptureImages();

        // Assert: the failure took the storage offline, once, as a journal flush whose message
        // names the drain's write, and it is no active bracket's to end.
        error.InnerException.ShouldBeOfType<IOException>();
        error.Cause.ShouldBe(StorageOfflineCause.JournalFlush);
        error.Message.ShouldContain("a write of the journal");
        raised.ShouldHaveSingleItem().ShouldBeSameAs(storage.OfflineError);
        storage.OfflineError!.Cause.ShouldBe(StorageOfflineCause.JournalFlush);
        storage.IsOffline.ShouldBeTrue();
        storage.ActiveTransactionCount.ShouldBe(0);
        journal.FailWrites.ShouldBe(0);
        using var reopened = FailureStorage.Open(images);
        reopened.ReadAllText().ShouldBe(["kept"]);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Journal failure: a rollback record whose drain fails still ends the transaction")]
    public void Rollback_DrainFails_ShouldEndTheTransactionAndGoOffline()
    {
        // Arrange: a bracket that changed a page, and a second bracket whose begin record is still
        // in the buffer, so the first bracket's rollback record has to drain it.
        using var storage = FailureStorage.Create(out var journal);
        storage.Insert("kept");
        var transaction = storage.BeginTransaction();
        storage.Insert(transaction, "rolled back");
        var other = storage.BeginTransaction();
        journal.FailWrites = 1;

        // Act
        var error = Should.Throw<StorageOfflineException>(() => transaction.Rollback());
        bool activeAfterRollback = transaction.IsActive;
        var images = storage.CaptureImages();
        transaction.Dispose();

        // Assert: the pages are restored and the transaction is over, so disposing it changes
        // nothing; the storage is offline, and the reopen holds only the committed row.
        error.InnerException.ShouldBeOfType<IOException>();
        activeAfterRollback.ShouldBeFalse();
        storage.ReadAllText().ShouldBe(["kept"]);
        storage.ActiveTransactionCount.ShouldBe(1);
        Should.Throw<StorageOfflineException>(() => other.Rollback());
        other.IsActive.ShouldBeFalse();
        using var reopened = FailureStorage.Open(images);
        reopened.ReadAllText().ShouldBe(["kept"]);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Journal failure: a full page image whose drain fails leaves its page unlocked")]
    public void InsertRecord_DrainFails_ShouldLeaveThePageUnlockedAndGoOffline()
    {
        // Arrange: after a checkpoint the insert's page is at the redo point, so its first touch
        // journals the page's full image (#1253), the first append that must drain (the begin
        // record ahead of it is buffered).
        using var storage = FailureStorage.Create(out var journal);
        var (pageId, _) = storage.Insert("kept");
        storage.Checkpoint();
        var transaction = storage.BeginTransaction();
        journal.FailWrites = 1;

        // Act
        Should.Throw<StorageOfflineException>(() => storage.Insert(transaction, "failed"));
        bool locked = storage.IsPageWriteLocked(pageId);
        Should.Throw<StorageOfflineException>(() => transaction.Rollback());

        // Assert: the failed touch released the page it locked before its append failed, and the
        // rollback ended the bracket though its record was refused.
        locked.ShouldBeFalse();
        transaction.IsActive.ShouldBeFalse();
        storage.ActiveTransactionCount.ShouldBe(0);
        storage.IsOffline.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Journal failure: a torn drain takes the journal offline and the reopen appends after the frames that verify")]
    public void Drain_WriteTornPartway_ShouldGoOfflineAndLeaveATailTheReopenCutsOff()
    {
        // Arrange: a written record, then two buffered ones whose drain tears half-way.
        var stream = new FaultingMemoryStream();
        var journal = StorageJournal.Create(stream, leaveOpen: true);
        journal.AppendBegin(1);
        journal.Flush();
        long lengthBefore = stream.Length;
        journal.AppendOperation(1, new byte[100]);
        journal.AppendCommit(1);
        stream.TearWrites = 1;
        int offlineRaised = 0;
        journal.Offline = _ => offlineRaised++;

        // Act
        var error = Should.Throw<StorageOfflineException>(() => journal.Flush());
        long lengthAfterFailure = stream.Length;
        var refusals = new Exception[]
        {
            Should.Throw<StorageOfflineException>(() => journal.AppendBegin(2)),
            Should.Throw<StorageOfflineException>(() => journal.Flush()),
            Should.Throw<StorageOfflineException>(() => journal.Checkpoint([])),
        };
        var readOffline = journal.ReadAll();
        journal.Dispose();
        long lengthAfterClose = stream.Length;
        using var reopened = StorageJournal.Create(new MemoryStream(stream.ToArray()));
        var recovered = reopened.ReadAll();
        reopened.AppendBegin(3);
        reopened.Flush();

        // Assert: half of the drain reached the stream and nothing after the failure did, not
        // even at the close; the offline journal reads what the medium holds; the reopened one
        // reads the frame before the tear and puts its next frame where the tear began.
        error.InnerException.ShouldBeOfType<IOException>();
        offlineRaised.ShouldBe(1);
        refusals.ShouldAllBe(refusal => refusal.InnerException == error.InnerException);
        lengthAfterFailure.ShouldBeGreaterThan(lengthBefore);
        lengthAfterClose.ShouldBe(lengthAfterFailure);
        readOffline.Select(record => record.Lsn).ShouldBe([1L]);
        recovered.Select(record => record.Lsn).ShouldBe([1L]);
        reopened.ReadAll().Select(record => (record.Lsn, record.TransactionSequence, record.Type)).ShouldBe(
        [
            (1L, 1L, JournalRecordType.BeginTransaction),
            (2L, 3L, JournalRecordType.BeginTransaction),
        ]);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Journal failure: an offline journal confirms only what was already durable or written")]
    public void EnsureDurable_AfterADrainFailure_ShouldConfirmOnlyEarlierLsns()
    {
        // Arrange
        var stream = new FaultingMemoryStream();
        using var journal = StorageJournal.Create(new StorageStream(new SimulatedDurableFileHandle(stream)));
        long durable = journal.AppendCommit(1);
        journal.EnsureDurable(durable);
        long written = journal.AppendCommit(2);
        journal.EnsureWritten(written);
        long buffered = journal.AppendCommit(3);
        stream.FailWrites = 1;

        // Act
        Should.Throw<StorageOfflineException>(() => journal.EnsureDurable(buffered));

        // Assert: an LSN made durable, or written, before the failure is still confirmed; the
        // buffered one is not, and it never reaches the medium.
        Should.NotThrow(() => journal.EnsureDurable(durable));
        Should.NotThrow(() => journal.EnsureWritten(written));
        Should.Throw<StorageOfflineException>(() => journal.EnsureWritten(buffered));
        journal.WrittenLsn.ShouldBe(written);
        journal.LastLsn.ShouldBe(buffered);
        journal.ReadAll().Select(record => record.Lsn).ShouldBe([durable, written]);
    }

    /// <summary>
    /// A storage whose journal fails writes on demand, with an append buffer of one small frame:
    /// every append drains the buffered frame ahead of it, and a page image is written directly.
    /// </summary>
    private sealed class FailureStorage : Storage
    {
        private FailureStorage(StorageStream data, StorageStream journal)
            : base(StorageModel.Custom, data, journal, new StorageStream(new MemoryStream()))
        {
        }

        public StorageJournal Log => WriteAheadLog;

        private MemoryStream DataStream { get; init; } = null!;

        private FaultingMemoryStream JournalStream { get; init; } = null!;

        public int ActiveTransactionCount => ActiveTransactions;

        public static FailureStorage Create(out FaultingMemoryStream journal)
        {
            var data = new MemoryStream();
            journal = new FaultingMemoryStream();
            var storage = new FailureStorage(
                new StorageStream(new SimulatedDurableFileHandle(data)),
                new StorageStream(new SimulatedDurableFileHandle(journal)))
            {
                DataStream = data,
                JournalStream = journal,
            };
            storage.InitializeNew((Name)"journal-failure-harness");
            storage.Log.MaximumBufferBytes = 64;
            return storage;
        }

        public static FailureStorage Open((byte[] Data, byte[] Journal) images)
        {
            var data = new MemoryStream();
            data.Write(images.Data);
            var journal = new FaultingMemoryStream();
            journal.Write(images.Journal);
            var storage = new FailureStorage(
                new StorageStream(new SimulatedDurableFileHandle(data)),
                new StorageStream(new SimulatedDurableFileHandle(journal)))
            {
                DataStream = data,
                JournalStream = journal,
            };
            storage.OpenExisting();
            return storage;
        }

        /// <summary>Gets what the files hold right now: what a process crash would leave.</summary>
        public (byte[] Data, byte[] Journal) CaptureImages() => (DataStream.ToArray(), JournalStream.ToArray());

        public (PageId PageId, int SlotIndex) Insert(StorageTransaction transaction, string text)
            => InsertRecord(transaction, Encoding.UTF8.GetBytes(text));

        public (PageId PageId, int SlotIndex) Insert(string text)
        {
            using var transaction = BeginTransaction();
            var location = Insert(transaction, text);
            transaction.Commit();
            return location;
        }

        public string[] ReadAllText()
        {
            var texts = new System.Collections.Generic.List<string>();
            using var iterator = GetUnitIterator();
            while (iterator.MoveNext())
            {
                texts.Add(Encoding.UTF8.GetString(iterator.Current.Data.Span));
            }

            return [.. texts];
        }
    }
}
