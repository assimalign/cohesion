using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
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
