using System;
using System.Buffers.Binary;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage.Units;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Storage.Tests;

public sealed partial class StorageCompactionTests
{
    private const ulong Owner = 7;

    [Theory(DisplayName = "Cohesion Test [Storage] - Compaction: borrowed input survives moves across its original address (#1161)")]
    [InlineData(false)]
    [InlineData(true)]
    public void Write_AliasedInput_ShouldPreserveTheOriginalBytes(bool insert)
    {
        // Arrange: a patterned source shifts over its original address during packing.
        using var storage = RecordStorage.Create();
        int firstLength = insert ? 2000 : 1000;
        byte[] source = new byte[3000];
        for (int i = 0; i < source.Length; i++) { source[i] = (byte)(i % 251); }
        (PageId PageId, int SlotIndex) first;
        using (var bracket = storage.BeginTransaction())
        {
            first = storage.Insert(bracket, Owner, Filled(firstLength, 0x11));
            storage.Insert(bracket, Owner, source).PageId.ShouldBe(first.PageId);
            storage.Insert(bracket, Owner, Filled(insert ? 2000 : 2800, 0x77)).PageId.ShouldBe(first.PageId);
            bracket.Commit();
        }
        if (insert)
        {
            using var bracket = storage.BeginTransaction();
            storage.Delete(bracket, first.PageId, first.SlotIndex);
            bracket.Commit();
        }
        int length = insert ? 1500 : 2000;
        byte[] expected = source.AsSpan(0, length).ToArray();
        using var pin = storage.PageManager.GetPage(first.PageId);
        (PageId PageId, int SlotIndex) result = first;

        // Act: use a real borrowed page span, not a pre-copied record.
        using (var bracket = storage.BeginTransaction())
        {
            var bytes = pin.Page.AsSpan().Slice(Page.HeaderSize + firstLength, length);
            if (insert) { result = storage.Insert(bracket, Owner, bytes); }
            else { storage.Update(bracket, first.PageId, first.SlotIndex, bytes); }
            bracket.Commit();
        }

        // Assert
        result.PageId.ShouldBe(first.PageId);
        storage.Read(result.PageId, result.SlotIndex).Span.SequenceEqual(expected).ShouldBeTrue();
        storage.GetOwnerPages(Owner).ShouldHaveSingleItem().ShouldBe(first.PageId);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Compaction: insufficient packed capacity leaves the page body and directory unchanged (#1161)")]
    public void UpdateRecord_ReplacementStillDoesNotFit_ShouldLeavePageUnchanged()
    {
        // Arrange
        using var storage = RecordStorage.Create();
        (PageId PageId, int SlotIndex) target;
        using (var bracket = storage.BeginTransaction())
        {
            target = storage.Insert(bracket, Owner, Filled(1000, 0x11));
            storage.Insert(bracket, Owner, Filled(6500, 0x33)).PageId.ShouldBe(target.PageId);
            bracket.Commit();
        }
        using var pin = storage.PageManager.GetPage(target.PageId);
        byte[] before = pin.Page.AsSpan().Slice(Page.HeaderSize).ToArray();
        ushort freeEnd = new SlottedPage(pin.Page).FreeDataEnd;

        // Act
        using var transaction = storage.BeginTransaction();
        Should.Throw<SlottedPageException>(() => storage.Update(transaction, target.PageId, target.SlotIndex, Filled(2000, 0x77)));

        // Assert
        pin.Page.AsSpan().Slice(Page.HeaderSize).SequenceEqual(before).ShouldBeTrue();
        new SlottedPage(pin.Page).FreeDataEnd.ShouldBe(freeEnd);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Compaction: an overlapping replacement target is validated before it is retired (#1161)")]
    public void UpdateRecord_OverlappingTarget_ShouldFailBeforeMovingBytes()
    {
        // Arrange
        using var storage = RecordStorage.Create();
        (PageId PageId, int SlotIndex) target;
        using (var bracket = storage.BeginTransaction())
        {
            target = storage.Insert(bracket, Owner, Filled(1000, 0x11));
            storage.Insert(bracket, Owner, Filled(5800, 0x33)).PageId.ShouldBe(target.PageId);
            bracket.Commit();
        }
        using var transaction = storage.BeginTransaction();
        using (var write = storage.OpenPageForWrite(transaction, target.PageId))
        {
            BinaryPrimitives.WriteUInt16LittleEndian(write.Page.AsSpan().Slice(
                Page.Size - ((target.SlotIndex + 1) * 4), 2), Page.HeaderSize + 1000);
            write.MarkDirty();
        }
        using var pin = storage.PageManager.GetPage(target.PageId);
        byte[] before = pin.Page.AsSpan().Slice(Page.HeaderSize).ToArray();

        // Act
        Should.Throw<StorageCorruptionException>(() => storage.Update(transaction, target.PageId, target.SlotIndex, Filled(2000, 0x77)));

        // Assert
        pin.Page.AsSpan().Slice(Page.HeaderSize).SequenceEqual(before).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Compaction: rejected layouts release the selection pin and mutable scope (#1161)")]
    public void InsertRecord_CorruptCompactionPlan_ShouldReleaseItsPin()
    {
        // Arrange
        using var storage = RecordStorage.Create();
        (PageId PageId, int SlotIndex) kept;
        using (var bracket = storage.BeginTransaction())
        {
            storage.Insert(bracket, Owner, Filled(4000, 0x11));
            kept = storage.Insert(bracket, Owner, Filled(3000, 0x33));
            bracket.Commit();
        }
        using var transaction = storage.BeginTransaction();
        using (var write = storage.OpenPageForWrite(transaction, kept.PageId))
        {
            // A live slot addressing the header must fail before any compaction move.
            BinaryPrimitives.WriteUInt16LittleEndian(write.Page.AsSpan().Slice(
                Page.Size - ((kept.SlotIndex + 1) * 4), 2), 0);
            write.MarkDirty();
        }
        using var pin = storage.PageManager.GetPage(kept.PageId);
        int before = pin.Entry.PinCount;

        // Act
        Should.Throw<StorageCorruptionException>(() => storage.Insert(transaction, Owner, Filled(1500, 0x77)));

        // Assert: disposal of the active transaction later repairs the injected damage.
        pin.Entry.PinCount.ShouldBe(before);
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Compaction: grow/shrink cycles retain one page and stable slots (#1161)")]
    [InlineData(5000)]
    [InlineData(5800)]
    public void UpdateRecord_GrowShrinkCycles_ShouldKeepTheOriginalPageUsable(int neighbourLength)
    {
        // Arrange: the second shape needs the replaced record's old space as well.
        using var storage = RecordStorage.Create();
        byte[] small = Filled(1000, 0x11);
        byte[] large = Filled(2000, 0x77);
        byte[] neighbour = Filled(neighbourLength, 0x33);
        (PageId PageId, int SlotIndex) target, kept;
        using (var bracket = storage.BeginTransaction())
        {
            target = storage.Insert(bracket, Owner, small);
            kept = storage.Insert(bracket, Owner, neighbour);
            bracket.Commit();
        }
        kept.PageId.ShouldBe(target.PageId);

        // Act: a fixed amount of work, independent of CPU speed or wall time.
        using (var bracket = storage.BeginTransaction())
        {
            for (int cycle = 0; cycle < 512; cycle++)
            {
                storage.Update(bracket, target.PageId, target.SlotIndex, large);
                storage.Read(target.PageId, target.SlotIndex).Span.SequenceEqual(large).ShouldBeTrue();
                storage.Update(bracket, target.PageId, target.SlotIndex, small);
                storage.Read(kept.PageId, kept.SlotIndex).Span.SequenceEqual(neighbour).ShouldBeTrue();
            }
            bracket.Commit();
        }

        // Assert: physical relocation cannot invalidate either stored reference.
        storage.Read(target.PageId, target.SlotIndex).Span.SequenceEqual(small).ShouldBeTrue();
        storage.GetOwnerPages(Owner).ShouldHaveSingleItem().ShouldBe(target.PageId);
        using var pin = storage.PageManager.GetPage(target.PageId);
        new SlottedPage(pin.Page).SlotCount.ShouldBe((ushort)2);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Compaction: insertion reclaims dead bytes in the current page without reusing slot identities (#1161)")]
    public void InsertRecord_FragmentedCurrentPage_ShouldCompactBeforeAllocating()
    {
        // Arrange
        using var storage = RecordStorage.Create();
        byte[] neighbour = Filled(3000, 0x33);
        (PageId PageId, int SlotIndex) deleted, kept;
        using (var bracket = storage.BeginTransaction())
        {
            deleted = storage.Insert(bracket, Owner, Filled(4000, 0x11));
            kept = storage.Insert(bracket, Owner, neighbour);
            bracket.Commit();
        }
        using (var bracket = storage.BeginTransaction())
        {
            storage.Delete(bracket, deleted.PageId, deleted.SlotIndex);
            bracket.Commit();
        }

        // Act
        (PageId PageId, int SlotIndex) inserted;
        byte[] payload = Filled(1500, 0x77);
        using (var bracket = storage.BeginTransaction())
        {
            inserted = storage.Insert(bracket, Owner, payload);
            bracket.Commit();
        }

        // Assert
        inserted.PageId.ShouldBe(kept.PageId);
        inserted.SlotIndex.ShouldBe(2);
        storage.TryReadRecord(deleted.PageId, deleted.SlotIndex, Owner, out _).ShouldBeFalse();
        storage.Read(kept.PageId, kept.SlotIndex).Span.SequenceEqual(neighbour).ShouldBeTrue();
        storage.Read(inserted.PageId, inserted.SlotIndex).Span.SequenceEqual(payload).ShouldBeTrue();
        storage.GetOwnerPages(Owner).ShouldHaveSingleItem().ShouldBe(kept.PageId);
    }

    private static byte[] Filled(int length, byte value)
    {
        byte[] bytes = new byte[length];
        bytes.AsSpan().Fill(value);
        return bytes;
    }

    private sealed class RecordStorage : Storage
    {
        private RecordStorage() : this(
            new StorageStream(new SimulatedDurableFileHandle()),
            new StorageStream(new SimulatedDurableFileHandle()),
            new StorageStream(new SimulatedDurableFileHandle()), 16) { }

        private RecordStorage(StorageStream data, StorageStream journal, StorageStream backup, int capacity)
            : base(StorageModel.Custom, data, journal, backup, bufferPoolCapacity: capacity) { }

        internal static RecordStorage Create()
        {
            var storage = new RecordStorage();
            storage.InitializeNew((Name)"compaction");
            return storage;
        }

        internal static RecordStorage Create(CrashSimulationStream data, CrashSimulationStream journal,
            CrashSimulationStream backup, int capacity)
        {
            var storage = new RecordStorage(new StorageStream(data), new StorageStream(journal),
                new StorageStream(backup), capacity);
            storage.InitializeNew((Name)"compaction");
            return storage;
        }

        internal static RecordStorage Open(byte[] data, byte[] journal, byte[] backup)
        {
            var storage = new RecordStorage(new StorageStream(new CrashSimulationStream(data, writeThrough: true)),
                new StorageStream(new CrashSimulationStream(journal)),
                new StorageStream(new CrashSimulationStream(backup, writeThrough: true)), 16);
            storage.OpenExisting();
            return storage;
        }

        internal (PageId PageId, int SlotIndex) Insert(StorageTransaction transaction, ulong owner, ReadOnlySpan<byte> data)
            => InsertRecord(transaction, owner, data);

        internal void Update(StorageTransaction transaction, PageId page, int slot, ReadOnlySpan<byte> data)
            => UpdateRecord(transaction, page, slot, data);

        internal void Delete(StorageTransaction transaction, PageId page, int slot)
            => DeleteRecord(transaction, page, slot);

        internal ReadOnlyMemory<byte> Read(PageId page, int slot) => ReadRecord(page, slot);
    }
}
