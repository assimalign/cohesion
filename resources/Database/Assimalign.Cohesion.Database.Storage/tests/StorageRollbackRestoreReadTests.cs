using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage.Units;

namespace Assimalign.Cohesion.Database.Storage.Tests;

/// <summary>
/// A rollback restores each page it changed in place, and a record read holds a pin, not a latch,
/// so committed records that share a page with a failed bracket's inserts are read while that page
/// is rewritten. Until the #1371 review the restore cleared the page and then wrote the pre-image's
/// runs back, and a read meanwhile found the page's type cleared or its slot gone: it reported a
/// committed record as reclaimed, and every model above skipped the row without an error. The
/// restore now builds the page aside, copies it over the live page in one pass, and brackets the
/// copy with the page's restore sequence, which <see cref="Storage.TryReadRecord(PageId, int, ulong, out ReadOnlyMemory{byte})"/>,
/// the model storages' direct record read and the unit iterator confirm their read against.
/// </summary>
public sealed class StorageRollbackRestoreReadTests
{
    private const ulong Owner = 7;
    private const int CommittedRecords = 30;
    private const int RecordLength = 128;
    private const int FailedInserts = 20;
    private static readonly TimeSpan RaceDuration = TimeSpan.FromSeconds(2);

    /// <summary>
    /// A writer inserts records onto the page that holds 30 committed ones and rolls the bracket
    /// back, over and over, while readers read the committed records through their references.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Storage] - Rollback restore: reads of committed records on a page that rollbacks keep restoring find every record (#1371)")]
    public async Task TryReadRecord_RacingRollbacksOfTheRecordsPage_ShouldFindEveryCommittedRecord()
    {
        // Arrange
        using var storage = TornStorage.Create(poolCapacity: 16);
        var committed = CommitRecords(storage);
        using var stop = new CancellationTokenSource(RaceDuration);
        var race = new Race(stop);
        var writer = Task.Run(() => race.RollBackInserts(storage, committed[0].PageId));

        // Act: two readers through TryReadRecord, one through the storages' direct ReadRecord.
        var readers = new Task[3];
        for (int reader = 0; reader < readers.Length; reader++)
        {
            int seed = reader + 1;
            bool direct = reader == readers.Length - 1;
            readers[reader] = Task.Run(() => race.Run(() =>
            {
                var random = new Random(seed);
                while (race.Running)
                {
                    int index = random.Next(committed.Count);
                    var (pageId, slotIndex) = committed[index];
                    if (direct)
                    {
                        if (Id(storage.ReadBytes(pageId, slotIndex)) != index)
                        {
                            race.Fail(new InvalidOperationException($"Committed record {index} at page {(long)pageId} slot {slotIndex} read directly as another record."));
                        }
                    }
                    else if (!storage.TryReadRecord(pageId, slotIndex, Owner, out var record) || Id(record.Span) != index)
                    {
                        race.Fail(new InvalidOperationException($"Committed record {index} at page {(long)pageId} slot {slotIndex} read as reclaimed or as another record."));
                    }

                    race.CountRead();
                }
            }));
        }

        await Task.WhenAll([writer, .. readers]);

        // Assert: the race ran on both sides and no read missed a committed record.
        race.Failure.ShouldBeNull();
        race.Rollbacks.ShouldBeGreaterThan(0);
        race.Reads.ShouldBeGreaterThan(0);
    }

    /// <summary>
    /// The same race, read by owner-scoped scans instead of references: every scan returns each of
    /// the 30 committed records once.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Storage] - Rollback restore: scans of a page that rollbacks keep restoring return every committed record (#1371)")]
    public async Task OwnerScan_RacingRollbacksOfTheRecordsPage_ShouldReturnEveryCommittedRecord()
    {
        // Arrange
        using var storage = TornStorage.Create(poolCapacity: 16);
        var committed = CommitRecords(storage);
        using var stop = new CancellationTokenSource(RaceDuration);
        var race = new Race(stop);
        var writer = Task.Run(() => race.RollBackInserts(storage, committed[0].PageId));

        // Act
        var readers = new Task[2];
        for (int reader = 0; reader < readers.Length; reader++)
        {
            readers[reader] = Task.Run(() => race.Run(() =>
            {
                while (race.Running)
                {
                    // The storage has no visibility: a scan also returns the open bracket's
                    // records (ids from 1,000), which the models above filter by their stamps.
                    var records = storage.ScanOwner(Owner);
                    var committedIds = new SortedSet<int>();
                    bool malformed = false;
                    foreach (var record in records)
                    {
                        int id = Id(record);
                        malformed |= id < 0;
                        if (id is >= 0 and < CommittedRecords)
                        {
                            malformed |= !committedIds.Add(id);
                        }
                    }

                    if (malformed || committedIds.Count != CommittedRecords)
                    {
                        race.Fail(new InvalidOperationException(
                            $"A scan returned {committedIds.Count} of the {CommittedRecords} committed records [{string.Join(", ", committedIds)}]{(malformed ? ", and a torn or repeated record" : "")}."));
                    }

                    race.CountRead();
                }
            }));
        }

        await Task.WhenAll([writer, .. readers]);

        // Assert
        race.Failure.ShouldBeNull();
        race.Rollbacks.ShouldBeGreaterThan(0);
        race.Reads.ShouldBeGreaterThan(0);
    }

    /// <summary>
    /// The reader side of the restore sequence, without a race: while the page's restore is open
    /// and the page is cleared, as the old restore left it between its clear and its runs, a read
    /// of a committed record does not complete; once the page is whole and the restore closed, it
    /// finds the record. Before the fix the read returned false at once.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Storage] - Rollback restore: a record read waits out a restore of its page and then finds the record (#1371)")]
    public async Task TryReadRecord_WhileItsPageIsBeingRestored_ShouldWaitAndFindTheRecord()
    {
        // Arrange
        using var storage = TornStorage.Create(poolCapacity: 16);
        var committed = CommitRecords(storage);
        var (pageId, slotIndex) = committed[5];

        // Act
        var (completedWhileRestoring, read) = WhileRestoring(storage, pageId, () =>
        {
            bool found = storage.TryReadRecord(pageId, slotIndex, Owner, out var record);
            return found ? Id(record.Span) : -1;
        });
        int id = await read.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        completedWhileRestoring.ShouldBeFalse("the read waits while the restore is open");
        id.ShouldBe(5);
    }

    /// <summary>
    /// The same for the direct read the model storages use for content chunks and entries
    /// (<c>ReadRecord</c>): it waits out the restore instead of failing on the cleared page's
    /// missing slot.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Storage] - Rollback restore: a direct record read waits out a restore of its page instead of failing (#1371)")]
    public async Task ReadRecord_WhileItsPageIsBeingRestored_ShouldWaitAndReturnTheRecord()
    {
        // Arrange
        using var storage = TornStorage.Create(poolCapacity: 16);
        var committed = CommitRecords(storage);
        var (pageId, slotIndex) = committed[7];

        // Act
        var (completedWhileRestoring, read) = WhileRestoring(storage, pageId, () => Id(storage.ReadBytes(pageId, slotIndex)));
        int id = await read.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        completedWhileRestoring.ShouldBeFalse("the read waits while the restore is open");
        id.ShouldBe(7);
    }

    /// <summary>
    /// The same for a scan: an owner-scoped scan that reaches a page whose restore is open waits
    /// for it, and then returns every committed record on the page.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Storage] - Rollback restore: a scan waits out a restore of its page and then returns every record (#1371)")]
    public async Task OwnerScan_WhileItsPageIsBeingRestored_ShouldWaitAndReturnEveryRecord()
    {
        // Arrange
        using var storage = TornStorage.Create(poolCapacity: 16);
        var committed = CommitRecords(storage);

        // Act
        var (completedWhileRestoring, scan) = WhileRestoring(storage, committed[0].PageId, () => storage.ScanOwner(Owner).Count);
        int count = await scan.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        completedWhileRestoring.ShouldBeFalse("the scan waits while the restore is open");
        count.ShouldBe(CommittedRecords);
    }

    /// <summary>
    /// Opens the page's restore sequence and clears the page, starts <paramref name="read"/> on
    /// another thread, gives it 200 ms, then writes the page back whole and closes the restore.
    /// Synchronous, because the page is changed through its pointer.
    /// </summary>
    private static unsafe (bool CompletedWhileRestoring, Task<int> Read) WhileRestoring(TornStorage storage, PageId pageId, Func<int> read)
    {
        byte[] whole = storage.PageBytes(pageId);
        using var handle = storage.PageManager.GetPage(pageId);
        var page = new Span<byte>(handle.Page.Pointer, Page.Size);
        handle.Entry.BeginRestore();
        try
        {
            page.Clear();
            var task = Task.Run(read);
            bool completed = SpinWait.SpinUntil(() => task.IsCompleted, TimeSpan.FromMilliseconds(200));
            whole.CopyTo(page);
            return (completed, task);
        }
        finally
        {
            handle.Entry.EndRestore();
        }
    }

    /// <summary>
    /// Commits 30 records of <see cref="RecordLength"/> bytes, each carrying its index, onto one page
    /// of the owner's chain, leaving room on the page for a failed bracket's inserts.
    /// </summary>
    private static List<(PageId PageId, int SlotIndex)> CommitRecords(TornStorage storage)
    {
        var locations = new List<(PageId PageId, int SlotIndex)>(CommittedRecords);
        using (var transaction = storage.BeginTransaction())
        {
            for (int index = 0; index < CommittedRecords; index++)
            {
                locations.Add(storage.Insert(transaction, Owner, Record(index)));
            }

            transaction.Commit();
        }

        locations.ShouldAllBe(location => location.PageId == locations[0].PageId);
        return locations;
    }

    /// <summary>A record that starts with its index and is filled with a byte derived from it.</summary>
    private static byte[] Record(int index)
    {
        var record = new byte[RecordLength];
        record.AsSpan().Fill((byte)(index + 1));
        BinaryPrimitives.WriteInt32LittleEndian(record, index);
        return record;
    }

    /// <summary>The index a record carries, or -1 when its bytes are not a record <see cref="Record"/> wrote.</summary>
    private static int Id(ReadOnlySpan<byte> record)
    {
        if (record.Length != RecordLength)
        {
            return -1;
        }

        int index = BinaryPrimitives.ReadInt32LittleEndian(record);
        if (index < 0 || index >= 1_000_000 || record[RecordLength - 1] != (byte)(index + 1))
        {
            return -1;
        }

        return index;
    }

    private sealed class Race(CancellationTokenSource stop)
    {
        private Exception? _failure;
        private long _rollbacks;
        private long _reads;

        public Exception? Failure => Volatile.Read(ref _failure);

        public long Rollbacks => Interlocked.Read(ref _rollbacks);

        public long Reads => Interlocked.Read(ref _reads);

        public bool Running => !stop.IsCancellationRequested && Failure is null;

        public void CountRead() => Interlocked.Increment(ref _reads);

        public void Fail(Exception exception) => Interlocked.CompareExchange(ref _failure, exception, null);

        public void Run(Action action)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
        }

        /// <summary>
        /// Inserts 20 records after the committed ones and rolls the bracket back, so every
        /// rollback restores the page that holds the committed records.
        /// </summary>
        public void RollBackInserts(TornStorage storage, PageId committedPage) => Run(() =>
        {
            while (Running)
            {
                using var transaction = storage.BeginTransaction();
                for (int index = 0; index < FailedInserts; index++)
                {
                    var location = storage.Insert(transaction, Owner, Record(1_000 + index));
                    if (location.PageId != committedPage)
                    {
                        throw new InvalidOperationException("A failed insert left the committed records' page.");
                    }
                }

                transaction.Rollback();
                Interlocked.Increment(ref _rollbacks);
            }
        });
    }
}
