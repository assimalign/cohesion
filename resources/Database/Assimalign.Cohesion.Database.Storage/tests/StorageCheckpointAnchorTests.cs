using System;
using System.IO;
using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Storage.Tests;

/// <summary>
/// The checkpoint anchor (#1226 integration review). A checkpoint truncates the journal and then
/// appends a record listing the logical transactions whose begin records the truncation destroyed.
/// When that append fails, or the process stops before the record is flushed, nothing in the
/// journal names those transactions any more; the list the checkpoint wrote into the file header
/// before the truncation still does.
/// </summary>
public sealed class StorageCheckpointAnchorTests
{
    [Fact(DisplayName = "Cohesion Test [Storage] - Checkpoint anchor: the active transactions a checkpoint records survive a crash")]
    public void Checkpoint_WithActiveTransactions_ShouldRecordThemInTheFileHeader()
    {
        // Arrange
        using var storage = AnchorStorage.Create();

        // Act
        storage.Checkpoint([5, 9]);
        var images = storage.CaptureImages();
        using var reopened = AnchorStorage.Open(images.Data, images.Journal);

        // Assert
        storage.CheckpointActiveTransactions.ShouldBe([5L, 9L]);
        reopened.CheckpointActiveTransactions.ShouldBe([5L, 9L]);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Checkpoint anchor: a checkpoint record lost after the truncation leaves the anchor")]
    public void Checkpoint_RecordAppendFailsAfterTheTruncation_ShouldKeepTheAnchor()
    {
        // Arrange: a journal with records, and a checkpoint whose own record cannot be appended.
        using var storage = AnchorStorage.Create();
        storage.Insert("kept");
        storage.JournalStream.FailWrites = 1;

        // Act
        Should.Throw<IOException>(() => storage.Checkpoint([7]));
        var images = storage.CaptureImages();
        using var reopened = AnchorStorage.Open(images.Data, images.Journal);

        // Assert: the truncation emptied the journal, and only the anchor still names the
        // transaction; the data the checkpoint flushed survives.
        storage.JournalStream.FailWrites.ShouldBe(0);
        images.Journal.ShouldBeEmpty();
        reopened.Log.ReadAll().ShouldBeEmpty();
        reopened.CheckpointActiveTransactions.ShouldBe([7L]);
        reopened.CountRecords().ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Checkpoint anchor: more active transactions than the anchor holds defer the checkpoint")]
    public void Checkpoint_MoreActiveTransactionsThanTheAnchorHolds_ShouldBeRefusedBeforeWritingAnything()
    {
        // Arrange
        using var storage = AnchorStorage.Create();
        storage.Checkpoint([3]);
        long lastLsn = storage.Log.LastLsn;
        long[] full = [.. Enumerable.Range(1, Storage.MaxCheckpointActiveTransactions).Select(i => (long)i)];
        long[] tooMany = [.. Enumerable.Range(1, Storage.MaxCheckpointActiveTransactions + 1).Select(i => (long)i)];

        // Act
        var refused = Should.Throw<StorageTransactionException>(() => storage.Checkpoint(tooMany));
        long lastLsnAfterRefusal = storage.Log.LastLsn;
        var anchorAfterRefusal = storage.CheckpointActiveTransactions;
        storage.Checkpoint(full);
        var images = storage.CaptureImages();
        using var reopened = AnchorStorage.Open(images.Data, images.Journal);

        // Assert: the refusal changed nothing, and a full anchor round-trips.
        refused.Message.ShouldContain("Checkpoint deferred");
        lastLsnAfterRefusal.ShouldBe(lastLsn);
        anchorAfterRefusal.ShouldBe([3L]);
        StorageFileHeader.CheckpointAnchorCapacity.ShouldBe(980);
        reopened.CheckpointActiveTransactions.ShouldBe(full);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Checkpoint anchor: every checkpoint replaces it, and a close that does not truncate keeps it")]
    public void Checkpoint_Later_ShouldReplaceTheAnchorWhichANonTruncatingCloseKeeps()
    {
        // Arrange
        using var storage = AnchorStorage.Create();
        storage.Checkpoint([11, 12]);
        storage.Checkpoint([12]);
        var replaced = storage.CheckpointActiveTransactions;

        // Act: a storage bracket is still active at the close, so the close flushes without a
        // checkpoint, and the anchor stays as the last checkpoint wrote it.
        var open = storage.BeginTransaction();
        storage.Dispose();
        var images = storage.CaptureImages();
        using var reopened = AnchorStorage.Open(images.Data, images.Journal);
        var anchorAfterTheClose = reopened.CheckpointActiveTransactions;
        reopened.Checkpoint();

        // Assert
        open.IsActive.ShouldBeTrue();
        replaced.ShouldBe([12L]);
        anchorAfterTheClose.ShouldBe([12L]);
        reopened.CheckpointActiveTransactions.ShouldBeEmpty();
    }

    /// <summary>A storage over byte buffers that survive its disposal, with a journal that can fail writes.</summary>
    private sealed class AnchorStorage : Storage
    {
        private readonly MemoryStream _data;
        private readonly FaultingMemoryStream _journal;

        private AnchorStorage(MemoryStream data, FaultingMemoryStream journal, bool reopen)
            : base(
                new StorageStream(new SimulatedDurableFileHandle(data)),
                new StorageStream(new SimulatedDurableFileHandle(journal)),
                new StorageStream(new MemoryStream()))
        {
            _data = data;
            _journal = journal;
            if (reopen)
            {
                OpenExisting(checkpointOnOpen: false);
            }
            else
            {
                InitializeNew((Name)"anchor-harness");
            }
        }

        public override StorageModel Model => StorageModel.Custom;

        public FaultingMemoryStream JournalStream => _journal;

        public IStorageJournal Log => WriteAheadLog;

        public static AnchorStorage Create() => new(new MemoryStream(), new FaultingMemoryStream(), reopen: false);

        public static AnchorStorage Open(byte[] data, byte[] journal) => new(Copy(data), CopyJournal(journal), reopen: true);

        /// <summary>Gets the bytes the storage has written so far, as a crash would leave them.</summary>
        public (byte[] Data, byte[] Journal) CaptureImages() => (_data.ToArray(), _journal.ToArray());

        public void Insert(string text)
        {
            using var transaction = BeginTransaction();
            InsertRecord(transaction, System.Text.Encoding.UTF8.GetBytes(text));
            transaction.Commit();
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

        private static MemoryStream Copy(byte[] bytes)
        {
            var stream = new MemoryStream();
            stream.Write(bytes);
            stream.Position = 0;
            return stream;
        }

        private static FaultingMemoryStream CopyJournal(byte[] bytes)
        {
            var stream = new FaultingMemoryStream();
            stream.Write(bytes);
            stream.Position = 0;
            return stream;
        }
    }
}
