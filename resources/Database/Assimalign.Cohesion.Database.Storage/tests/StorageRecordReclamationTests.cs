using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage.Units;

namespace Assimalign.Cohesion.Database.Storage.Tests;

/// <summary>
/// <see cref="Storage.TryReadRecord(PageId, int, ulong, out ReadOnlyMemory{byte})"/> (#1342): a
/// location whose record was reclaimed beneath the reference (a deleted or reverted slot, a freed
/// page, a page reallocated to another owner or as an index node) reads as <c>false</c>, and a page
/// that cannot be read (a failed checksum, a malformed slot, a short read) throws instead of reading
/// as reclaimed.
/// </summary>
public sealed class StorageRecordReclamationTests
{
    private const ulong Owner = 7;
    private const ulong OtherOwner = 9;

    [Fact(DisplayName = "Cohesion Test [Storage] - Reclaimed records: a live record reads back through both overloads")]
    public void TryReadRecord_LiveRecord_ShouldReturnACopy()
    {
        // Arrange
        using var storage = RecordStorage.Create(new CrashSimulationStream(), new CrashSimulationStream());
        var location = Insert(storage, Owner, "alpha");

        // Act
        bool owned = storage.TryReadRecord(location.PageId, location.SlotIndex, Owner, out var ownedRecord);
        bool any = storage.TryReadRecord(location.PageId, location.SlotIndex, out var anyRecord);

        // Assert
        owned.ShouldBeTrue();
        Text(ownedRecord).ShouldBe("alpha");
        any.ShouldBeTrue();
        Text(anyRecord).ShouldBe("alpha");
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Reclaimed records: a deleted slot reads as reclaimed and leaves its neighbours readable")]
    public void TryReadRecord_DeletedSlot_ShouldReturnFalse()
    {
        // Arrange: two records on one page, then the first one deleted.
        using var storage = RecordStorage.Create(new CrashSimulationStream(), new CrashSimulationStream());
        var deleted = Insert(storage, Owner, "alpha");
        var kept = Insert(storage, Owner, "beta");
        kept.PageId.ShouldBe(deleted.PageId);
        Delete(storage, deleted);

        // Act
        bool deletedRead = storage.TryReadRecord(deleted.PageId, deleted.SlotIndex, Owner, out var deletedRecord);
        bool keptRead = storage.TryReadRecord(kept.PageId, kept.SlotIndex, Owner, out var keptRecord);

        // Assert
        deletedRead.ShouldBeFalse();
        deletedRecord.IsEmpty.ShouldBeTrue();
        keptRead.ShouldBeTrue();
        Text(keptRecord).ShouldBe("beta");
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Reclaimed records: a slot a bracket rollback reverted out of existence reads as reclaimed")]
    public void TryReadRecord_SlotRevertedByRollback_ShouldReturnFalse()
    {
        // Arrange: the page stays allocated; only the second slot is reverted.
        using var storage = RecordStorage.Create(new CrashSimulationStream(), new CrashSimulationStream());
        var committed = Insert(storage, Owner, "alpha");
        (PageId PageId, int SlotIndex) reverted;
        using (var bracket = storage.BeginTransaction())
        {
            reverted = storage.Insert(bracket, Owner, Bytes("beta"));
            bracket.Rollback();
        }

        // Act
        bool read = storage.TryReadRecord(reverted.PageId, reverted.SlotIndex, Owner, out _);

        // Assert
        reverted.PageId.ShouldBe(committed.PageId);
        storage.FreeSpaceMap.IsAllocated(reverted.PageId).ShouldBeTrue();
        read.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Reclaimed records: a page freed with its last record reads as reclaimed")]
    public void TryReadRecord_FreedPage_ShouldReturnFalse()
    {
        // Arrange
        using var storage = RecordStorage.Create(new CrashSimulationStream(), new CrashSimulationStream());
        var location = Insert(storage, Owner, "alpha");
        Delete(storage, location);
        storage.FreeSpaceMap.IsAllocated(location.PageId).ShouldBeFalse();

        // Act
        bool owned = storage.TryReadRecord(location.PageId, location.SlotIndex, Owner, out _);
        bool any = storage.TryReadRecord(location.PageId, location.SlotIndex, out _);

        // Assert
        owned.ShouldBeFalse();
        any.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Reclaimed records: a page reallocated to another owner reads as reclaimed for the former owner")]
    public void TryReadRecord_PageReusedByAnotherOwner_ShouldReturnFalseForTheFormerOwner()
    {
        // Arrange: the freed page is the next one the allocator hands out.
        using var storage = RecordStorage.Create(new CrashSimulationStream(), new CrashSimulationStream());
        var former = Insert(storage, Owner, "alpha");
        Delete(storage, former);
        var reused = Insert(storage, OtherOwner, "gamma");
        reused.ShouldBe(former);

        // Act
        bool formerOwner = storage.TryReadRecord(former.PageId, former.SlotIndex, Owner, out _);
        bool newOwner = storage.TryReadRecord(former.PageId, former.SlotIndex, OtherOwner, out var newRecord);
        bool anyOwner = storage.TryReadRecord(former.PageId, former.SlotIndex, out var anyRecord);

        // Assert: only the owner overload can tell; the ownerless one leaves it to the stamps.
        formerOwner.ShouldBeFalse();
        newOwner.ShouldBeTrue();
        Text(newRecord).ShouldBe("gamma");
        anyOwner.ShouldBeTrue();
        Text(anyRecord).ShouldBe("gamma");
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Reclaimed records: a page reallocated as an index node reads as reclaimed, not as corruption")]
    public unsafe void TryReadRecord_PageReusedAsIndexNode_ShouldReturnFalse()
    {
        // Arrange: the freed data page comes back as a page whose header, read as a slotted
        // page, records more slots than a page can hold.
        using var storage = RecordStorage.Create(new CrashSimulationStream(), new CrashSimulationStream());
        var location = Insert(storage, Owner, "alpha");
        Delete(storage, location);
        using (var bracket = storage.BeginTransaction())
        {
            using (var node = storage.AllocatePageForWrite(bracket, PageType.Index))
            {
                node.Id.ShouldBe(location.PageId);
                ((Page.Header*)node.Page.Pointer)->SlotCount = ushort.MaxValue;
            }

            bracket.Commit();
        }

        // Act
        bool owned = storage.TryReadRecord(location.PageId, location.SlotIndex, Owner, out _);
        bool any = storage.TryReadRecord(location.PageId, location.SlotIndex, out _);

        // Assert
        owned.ShouldBeFalse();
        any.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Reclaimed records: a page that fails its checksum throws instead of reading as reclaimed")]
    public void TryReadRecord_PageFailsChecksum_ShouldThrowStorageCorruptionException()
    {
        // Arrange: a clean shutdown, then one byte of the record flipped on disk.
        var data = new CrashSimulationStream(writeThrough: true);
        var journal = new CrashSimulationStream();
        var storage = RecordStorage.Create(data, journal);
        var location = Insert(storage, Owner, "alpha-record");
        storage.Dispose();
        byte[] image = data.CaptureDurable();
        FlipRecordByte(image, location.PageId, "alpha-record");
        using var reopened = RecordStorage.Open(new CrashSimulationStream(image, writeThrough: true), new CrashSimulationStream(journal.CaptureDurable()));

        // Act
        var owned = Should.Throw<StorageCorruptionException>(() => reopened.TryReadRecord(location.PageId, location.SlotIndex, Owner, out _));
        var any = Should.Throw<StorageCorruptionException>(() => reopened.TryReadRecord(location.PageId, location.SlotIndex, out _));

        // Assert
        owned.PageId.ShouldBe(location.PageId);
        any.PageId.ShouldBe(location.PageId);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Reclaimed records: a page that ends inside the stream throws instead of reading as reclaimed")]
    public void TryReadRecord_PageReadEndsEarly_ShouldThrowStorageIOException()
    {
        // Arrange: a clean shutdown, then the data stream cut inside the record's page.
        var data = new CrashSimulationStream(writeThrough: true);
        var journal = new CrashSimulationStream();
        var storage = RecordStorage.Create(data, journal);
        var location = Insert(storage, Owner, "alpha");
        storage.Dispose();
        var cut = new CrashSimulationStream(data.CaptureDurable(), writeThrough: true);
        using var reopened = RecordStorage.Open(cut, new CrashSimulationStream(journal.CaptureDurable()));
        cut.SetLength(((long)location.PageId * Page.Size) + (Page.Size / 2));

        // Act
        var failure = Should.Throw<StorageIOException>(() => reopened.TryReadRecord(location.PageId, location.SlotIndex, Owner, out _));

        // Assert
        reopened.FreeSpaceMap.IsAllocated(location.PageId).ShouldBeTrue();
        failure.Message.ShouldContain("Unexpected end of stream", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Reclaimed records: a slot that addresses bytes past the page throws instead of reading as reclaimed")]
    public void TryReadRecord_SlotAddressesBytesPastThePage_ShouldThrowStorageCorruptionException()
    {
        // Arrange: the slot entry's offset moved to the last byte of the page.
        using var storage = RecordStorage.Create(new CrashSimulationStream(), new CrashSimulationStream());
        var location = Insert(storage, Owner, "alpha");
        using (var handle = storage.PageManager.GetPage(location.PageId))
        {
            var entry = handle.Page.AsSpan().Slice(Page.Size - ((location.SlotIndex + 1) * 4), 4);
            BinaryPrimitives.WriteUInt16LittleEndian(entry, Page.Size - 1);
        }

        // Act
        var failure = Should.Throw<StorageCorruptionException>(() => storage.TryReadRecord(location.PageId, location.SlotIndex, Owner, out _));

        // Assert
        failure.PageId.ShouldBe(location.PageId);
    }

    /// <summary>
    /// The page a reference names can be freed and reallocated between the read's allocation check
    /// and its pin. Before the fix the read caught the pin's "not allocated" failure and checked the
    /// map again, which by then allocated the page once more, so it threw a reclaimed record's page
    /// as an I/O error.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Storage] - Reclaimed records: a read racing the free and reallocation of its page reads as reclaimed, never as an error")]
    public async Task TryReadRecord_PageFreedAndReallocatedConcurrently_ShouldNeverThrow()
    {
        // Arrange: one record, then a writer that keeps freeing its page and taking it back.
        using var storage = RecordStorage.Create(new CrashSimulationStream(), new CrashSimulationStream());
        var stale = Insert(storage, Owner, "alpha");
        Delete(storage, stale);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var writer = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                Delete(storage, Insert(storage, OtherOwner, "gamma"));
            }
        });

        // Act
        long reads = 0;
        long found = 0;
        Exception? failure = null;
        while (!stop.IsCancellationRequested && failure is null)
        {
            try
            {
                if (storage.TryReadRecord(stale.PageId, stale.SlotIndex, Owner, out _))
                {
                    found++;
                }

                reads++;
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        }

        await writer;

        // Assert: the record is gone for good, and its page always belonged to another owner or none.
        failure.ShouldBeNull();
        found.ShouldBe(0);
        reads.ShouldBeGreaterThan(0);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Reclaimed records: a scan skips a slot deleted, a slot reverted and a page freed beneath its pinned page")]
    public void MoveNext_SlotsAndPageReclaimedBetweenCalls_ShouldSkipThem()
    {
        // Arrange: three records on one page, a fourth page-mate inserted and reverted, and the
        // scan standing on the first record with the page pinned.
        using var storage = RecordStorage.Create(new CrashSimulationStream(), new CrashSimulationStream());
        var first = Insert(storage, Owner, "alpha");
        var second = Insert(storage, Owner, "beta");
        var third = Insert(storage, Owner, "gamma");
        using var iterator = storage.GetUnitIterator(Owner);
        iterator.MoveNext().ShouldBeTrue();
        Text(iterator.Current.Data).ShouldBe("alpha");

        // Act: the purge deletes the second record, a failed statement's insert is reverted, and
        // then the purge deletes the rest, which frees the page under the scan's pin.
        Delete(storage, second);
        using (var bracket = storage.BeginTransaction())
        {
            storage.Insert(bracket, Owner, Bytes("delta"));
            bracket.Rollback();
        }

        bool movedToThird = iterator.MoveNext();
        string thirdText = Text(iterator.Current.Data);
        Delete(storage, first);
        Delete(storage, third);
        bool movedPastTheFreedPage = iterator.MoveNext();

        // Assert
        movedToThird.ShouldBeTrue();
        thirdText.ShouldBe("gamma");
        storage.FreeSpaceMap.IsAllocated(first.PageId).ShouldBeFalse();
        movedPastTheFreedPage.ShouldBeFalse();
    }

    /// <summary>
    /// A scan pins its page but takes no latch, so a writer deletes slots, reverts inserts and
    /// frees pages between the scan's checks of a slot and its read of it. Before the fix the scan
    /// read the slot count, the slot's length and the slot itself as three separate reads, and
    /// the allocation check and the pin as two, so a statement scanning beside the version purge
    /// failed with "Cannot read a deleted slot", an out-of-range slot index, or "Page N is not
    /// allocated". The writer here does what statements, failed statements and the purge do.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Storage] - Reclaimed records: a scan racing deletes, reverted inserts and page frees skips what they reclaim instead of failing")]
    public async Task MoveNext_RacingReclamation_ShouldSkipWhatTheWriterReclaims()
    {
        // Arrange
        using var storage = RecordStorage.Create(new CrashSimulationStream(), new CrashSimulationStream());
        byte[] payload = new byte[900];
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var writer = Task.Run(() =>
        {
            var live = new List<(PageId PageId, int SlotIndex)>();
            while (!stop.IsCancellationRequested)
            {
                using (var bracket = storage.BeginTransaction())
                {
                    for (int index = 0; index < 16; index++)
                    {
                        live.Add(storage.Insert(bracket, Owner, payload));
                    }

                    bracket.Commit();
                }

                using (var bracket = storage.BeginTransaction())
                {
                    storage.Insert(bracket, Owner, payload);
                    bracket.Rollback();
                }

                foreach (var location in live)
                {
                    Delete(storage, location);
                }

                live.Clear();
            }
        });

        // Act
        long scans = 0;
        Exception? failure = null;
        while (!stop.IsCancellationRequested && failure is null)
        {
            try
            {
                using var iterator = storage.GetUnitIterator(Owner);
                while (iterator.MoveNext())
                {
                }

                scans++;
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        }

        await writer;

        // Assert
        failure.ShouldBeNull();
        scans.ShouldBeGreaterThan(0);
    }

    private static (PageId PageId, int SlotIndex) Insert(RecordStorage storage, ulong owner, string text)
    {
        using var bracket = storage.BeginTransaction();
        var location = storage.Insert(bracket, owner, Bytes(text));
        bracket.Commit();
        return location;
    }

    private static void Delete(RecordStorage storage, (PageId PageId, int SlotIndex) location)
    {
        using var bracket = storage.BeginTransaction();
        storage.Delete(bracket, location.PageId, location.SlotIndex);
        bracket.Commit();
    }

    private static void FlipRecordByte(byte[] image, PageId pageId, string text)
    {
        int pageStart = checked((int)((long)pageId * Page.Size));
        int at = image.AsSpan(pageStart, Page.Size).IndexOf(Bytes(text));
        at.ShouldBeGreaterThanOrEqualTo(Page.HeaderSize);
        image[pageStart + at] ^= 0x5A;
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static string Text(ReadOnlyMemory<byte> record) => Encoding.UTF8.GetString(record.Span);

    /// <summary>A concrete storage exposing owner-scoped record writes; reads use the public base members.</summary>
    private sealed class RecordStorage : Storage
    {
        private RecordStorage(CrashSimulationStream data, CrashSimulationStream journal)
            : base(StorageModel.Custom, new StorageStream(data), new StorageStream(journal), new StorageStream(new MemoryStream()), bufferPoolCapacity: 16)
        {
        }

        public static RecordStorage Create(CrashSimulationStream data, CrashSimulationStream journal)
        {
            var storage = new RecordStorage(data, journal);
            storage.InitializeNew((Name)"reclamation");
            return storage;
        }

        public static RecordStorage Open(CrashSimulationStream data, CrashSimulationStream journal)
        {
            var storage = new RecordStorage(data, journal);
            storage.OpenExisting();
            return storage;
        }

        public (PageId PageId, int SlotIndex) Insert(StorageTransaction transaction, ulong ownerId, byte[] data)
            => InsertRecord(transaction, ownerId, data);

        public void Delete(StorageTransaction transaction, PageId pageId, int slotIndex)
            => DeleteRecord(transaction, pageId, slotIndex);
    }
}
