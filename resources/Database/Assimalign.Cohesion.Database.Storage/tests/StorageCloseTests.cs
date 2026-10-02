using System.Text;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Storage.Tests;

/// <summary>
/// What closing an opened storage writes. A file set nothing was written through
/// closes without writing, so an engine that refuses a database at open (an
/// unsupported data format, say) leaves its files exactly as it found them — and
/// a journal whose open-time checkpoint the owner deferred is not truncated
/// before the owner analyzed it. A storage that was written to still checkpoints
/// on close.
/// </summary>
public sealed class StorageCloseTests
{
    private sealed class CloseStorage : Storage
    {
        private CloseStorage(StorageStream data, StorageStream journal)
            : base(data, journal, new StorageStream(new System.IO.MemoryStream())) { }

        public override StorageModel Model => StorageModel.Custom;

        public static CloseStorage Create(CrashSimulationStream data, CrashSimulationStream journal)
        {
            var storage = new CloseStorage(new StorageStream(data), new StorageStream(journal));
            storage.InitializeNew((Name)"close-harness");
            return storage;
        }

        public static CloseStorage Open(CrashSimulationStream data, CrashSimulationStream journal, bool checkpointOnOpen)
        {
            var storage = new CloseStorage(new StorageStream(data), new StorageStream(journal));
            storage.OpenExisting(checkpointOnOpen);
            return storage;
        }

        public int JournalRecordCount => WriteAheadLog.ReadAll().Count;

        public void Insert(string text)
        {
            using var transaction = BeginTransaction();
            InsertRecord(transaction, Encoding.UTF8.GetBytes(text));
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
    }

    /// <summary>Builds a cleanly closed file set holding two committed records.</summary>
    private static (byte[] Data, byte[] Journal) CleanImage()
    {
        var data = new CrashSimulationStream(writeThrough: true);
        var journal = new CrashSimulationStream();

        using (var storage = CloseStorage.Create(data, journal))
        {
            storage.Insert("first");
            storage.Insert("second");
        }

        return (data.CaptureDurable(), journal.CaptureDurable());
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Close: an opened file set nothing was written through closes byte-identical")]
    public void Dispose_OpenedWithoutWrites_ShouldLeaveFilesByteIdentical()
    {
        // Arrange: a cleanly closed file set (its journal holds the close checkpoint).
        var (dataImage, journalImage) = CleanImage();
        var data = new CrashSimulationStream(dataImage, writeThrough: true);
        var journal = new CrashSimulationStream(journalImage);

        // Act: open the way engines do (open-time checkpoint deferred), read,
        // close — no transaction, reservation or checkpoint in between.
        using (var storage = CloseStorage.Open(data, journal, checkpointOnOpen: false))
        {
            storage.CountRecords().ShouldBe(2);
        }

        // Assert: neither the header (its modification time included) nor the
        // journal was rewritten.
        data.CaptureLive().ShouldBe(dataImage);
        journal.CaptureLive().ShouldBe(journalImage);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Close: a deferred open-time checkpoint is not performed by a close without writes")]
    public void Dispose_DeferredCheckpointWithoutWrites_ShouldKeepJournalForNextOpen()
    {
        // Arrange: a crash after a commit — the journal holds the transaction's
        // records, and the page write never reached the data file.
        var crashedData = new CrashSimulationStream(writeThrough: true);
        var crashedJournal = new CrashSimulationStream();
        var crashed = CloseStorage.Create(crashedData, crashedJournal);
        crashed.Insert("committed");
        byte[] journalImage = crashedJournal.CaptureDurable();

        // Act: open with the checkpoint deferred (the owner would analyze the
        // journal first; recovery redoes the commit), then close without writing.
        var data = new CrashSimulationStream(crashedData.CaptureDurable(), writeThrough: true);
        var journal = new CrashSimulationStream(journalImage);
        int recordsAtOpen;
        using (var storage = CloseStorage.Open(data, journal, checkpointOnOpen: false))
        {
            recordsAtOpen = storage.JournalRecordCount;
            storage.CountRecords().ShouldBe(1);
        }

        // Assert: the journal still holds every record the owner never got to
        // analyze, so the next open sees exactly what this one saw.
        recordsAtOpen.ShouldBeGreaterThan(1);
        journal.CaptureLive().ShouldBe(journalImage);
        using var reopened = CloseStorage.Open(
            new CrashSimulationStream(data.CaptureLive(), writeThrough: true),
            new CrashSimulationStream(journal.CaptureLive()),
            checkpointOnOpen: false);
        reopened.JournalRecordCount.ShouldBe(recordsAtOpen);
        reopened.CountRecords().ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Close: a file set written to since open still checkpoints on close")]
    public void Dispose_AfterWrite_ShouldCheckpoint()
    {
        // Arrange
        var (dataImage, journalImage) = CleanImage();
        var data = new CrashSimulationStream(dataImage, writeThrough: true);
        var journal = new CrashSimulationStream(journalImage);

        // Act: one committed write, then close.
        using (var storage = CloseStorage.Open(data, journal, checkpointOnOpen: false))
        {
            storage.Insert("third");
        }

        // Assert: the close checkpoint truncated the journal to its checkpoint
        // record, and the write survives the reopen.
        using var reopened = CloseStorage.Open(new CrashSimulationStream(data.CaptureDurable(), writeThrough: true), new CrashSimulationStream(journal.CaptureDurable()), checkpointOnOpen: false);
        reopened.JournalRecordCount.ShouldBe(1);
        reopened.CountRecords().ShouldBe(3);
    }
}
