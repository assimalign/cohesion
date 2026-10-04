using System;
using System.IO;
using System.Linq;
using System.Text;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Storage.Tests;

/// <summary>
/// What a journal write failure leaves behind (#1226). A storage bracket whose begin or rollback
/// record cannot be appended must not stay counted as active, or every later checkpoint is refused
/// until a restart; and a frame a failed append wrote only part of must not hide the frames
/// appended after it from recovery.
/// </summary>
public sealed class StorageJournalFailureTests
{
    [Fact(DisplayName = "Cohesion Test [Storage] - Journal failure: a begin record that cannot be appended leaves no active transaction")]
    public void BeginTransaction_BeginRecordAppendFails_ShouldLeaveNoActiveTransaction()
    {
        // Arrange
        using var storage = FailureStorage.Create(out var journal);
        journal.FailWrites = 1;

        // Act
        Should.Throw<IOException>(() => storage.BeginTransaction());

        // Assert: nothing is left counted, so a checkpoint runs and later transactions commit.
        storage.Checkpoint();
        storage.Insert("after");
        storage.CountRecords().ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Journal failure: a rollback record that cannot be appended still ends the transaction")]
    public void Rollback_RollbackRecordAppendFails_ShouldEndTheTransactionAndReleaseItsPages()
    {
        // Arrange
        using var storage = FailureStorage.Create(out var journal);
        storage.Insert("kept");
        var transaction = storage.BeginTransaction();
        var (pageId, _) = storage.Insert(transaction, "rolled back");
        journal.FailWrites = 1;

        // Act
        Should.Throw<IOException>(() => transaction.Rollback());
        bool activeAfterRollback = transaction.IsActive;
        storage.Checkpoint();
        var (nextPageId, _) = storage.Insert("next");
        transaction.Dispose();

        // Assert: the pages are restored and the transaction is over before anything disposes
        // it; its page lock and its place in the active count are gone, so a checkpoint runs and
        // another transaction writes the same page; disposing it afterwards changes nothing.
        activeAfterRollback.ShouldBeFalse();
        nextPageId.ShouldBe(pageId);
        storage.Checkpoint();
        storage.CountRecords().ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Journal failure: a before image that cannot be appended leaves its page unlocked")]
    public void InsertRecord_BeforeImageAppendFails_ShouldLeaveThePageUnlocked()
    {
        // Arrange
        using var storage = FailureStorage.Create(out var journal);
        var (pageId, _) = storage.Insert("kept");
        var transaction = storage.BeginTransaction();
        journal.FailWrites = 1;

        // Act: the page's before image is the insert's first journal write.
        Should.Throw<IOException>(() => storage.Insert(transaction, "failed"));
        transaction.Rollback();
        var (nextPageId, _) = storage.Insert("next");

        // Assert: the failed transaction never held the page, so the next one writes it.
        nextPageId.ShouldBe(pageId);
        storage.Checkpoint();
        storage.CountRecords().ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Journal failure: a torn append is cut off, so later records stay readable")]
    public void Append_WriteTornPartway_ShouldRemoveThePartialFrameSoLaterRecordsStayReadable()
    {
        // Arrange
        var stream = new FaultingMemoryStream();
        using var journal = new StreamJournal(stream, leaveOpen: true);
        journal.AppendBegin(1);
        long lengthBefore = stream.Length;
        stream.TearWrites = 1;

        // Act
        Should.Throw<IOException>(() => journal.AppendRollback(1));
        long lengthAfterFailure = stream.Length;
        journal.AppendBegin(2);
        journal.AppendCommit(2);

        // Assert: the half-written frame is gone, and the records appended after the failure are
        // read, also by a journal reopened over the same bytes.
        lengthAfterFailure.ShouldBe(lengthBefore);
        journal.ReadAll().Select(record => (record.Lsn, record.TransactionSequence, record.Type)).ShouldBe(
        [
            (1L, 1L, JournalRecordType.BeginTransaction),
            (2L, 2L, JournalRecordType.BeginTransaction),
            (3L, 2L, JournalRecordType.CommitTransaction),
        ]);
        using var reopened = new StreamJournal(new MemoryStream(stream.ToArray()));
        reopened.ReadAll().Select(record => record.Type).ShouldBe(
            [JournalRecordType.BeginTransaction, JournalRecordType.BeginTransaction, JournalRecordType.CommitTransaction]);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Journal failure: a partial frame that cannot be cut off stops appends until a checkpoint")]
    public void Append_PartialFrameCannotBeRemoved_ShouldRefuseAppendsUntilACheckpointTruncates()
    {
        // Arrange
        var stream = new FaultingMemoryStream();
        using var journal = new StreamJournal(new StorageStream(new SimulatedDurableFileHandle(stream)));
        journal.AppendBegin(1);
        stream.TearWrites = 1;
        stream.FailTruncations = 1;

        // Act
        Should.Throw<IOException>(() => journal.AppendCommit(1));
        var refused = Should.Throw<JournalException>(() => journal.AppendBegin(2));
        journal.Checkpoint([]);
        journal.AppendBegin(3);

        // Assert: nothing was appended after the partial frame, so no acknowledged record was ever
        // hidden behind it; the checkpoint's truncation removed it, and appends resumed.
        refused.Message.ShouldContain("refuses appends");
        journal.ReadAll().Select(record => (record.TransactionSequence, record.Type)).ShouldBe(
        [
            (0L, JournalRecordType.Checkpoint),
            (3L, JournalRecordType.BeginTransaction),
        ]);
    }

    /// <summary>A storage whose journal fails writes on demand.</summary>
    private sealed class FailureStorage : Storage
    {
        private FailureStorage(StorageStream data, StorageStream journal)
            : base(data, journal, new StorageStream(new MemoryStream()))
        {
        }

        public override StorageModel Model => StorageModel.Custom;

        public static FailureStorage Create(out FaultingMemoryStream journal)
        {
            journal = new FaultingMemoryStream();
            var storage = new FailureStorage(
                new StorageStream(new SimulatedDurableFileHandle()),
                new StorageStream(new SimulatedDurableFileHandle(journal)));
            storage.InitializeNew((Name)"journal-failure-harness");
            return storage;
        }

        public (PageId PageId, int SlotIndex) Insert(IStorageTransaction transaction, string text)
            => InsertRecord(transaction, Encoding.UTF8.GetBytes(text));

        public (PageId PageId, int SlotIndex) Insert(string text)
        {
            using var transaction = BeginTransaction();
            var location = Insert(transaction, text);
            transaction.Commit();
            return location;
        }

        public int CountRecords()
        {
            int count = 0;
            using var iterator = GetUnitIterator();
            while (iterator.MoveNext())
            {
                count++;
            }

            return count;
        }
    }
}
