using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage.Units;

namespace Assimalign.Cohesion.Database.Storage.Tests;

/// <summary>
/// The slotted-page record layout over raw page memory. Every page here sits between two
/// guard regions, so an operation that writes or reads outside the page is caught as a
/// changed guard byte instead of silently corrupting a neighbour (#1157).
/// </summary>
public sealed unsafe class SlottedPageTests
{
    private const int guardSize = 512;
    private const byte guardByte = 0xCD;
    private const long pageId = 42;

    [Fact(DisplayName = "Cohesion Test [Storage] - SlottedPage: a relocating update needs the whole record in free space, not just its growth")]
    public void UpdateSlot_GrowWithLessFreeSpaceThanTheRecord_ShouldThrowAndLeaveThePageUntouched()
    {
        // Arrange: the catalog's pattern under ALTER TABLE ADD/DROP COLUMN — one record
        // shrinks in place, then grows and relocates, until the page runs out of room.
        var memory = NewGuardedPage(out byte* page);
        var slotted = new SlottedPage(page);
        int slot = slotted.InsertSlot(new byte[200]);
        int relocations = 0;

        // Act / Assert: every relocation either lands inside the record area or is refused.
        while (true)
        {
            slotted.UpdateSlot(slot, Filled(180, 1));
            int freeSpace = slotted.FreeSpace;
            byte[] before = PageBytes(memory);

            if (freeSpace < 200)
            {
                // Growth (20 bytes) fits; the record (200 bytes) does not.
                Should.Throw<SlottedPageException>(() => slotted.UpdateSlot(slot, Filled(200, 2)));
                PageBytes(memory).ShouldBe(before);
                break;
            }

            slotted.UpdateSlot(slot, Filled(200, 2));
            relocations++;
            AssertGeometry(slotted);
        }

        relocations.ShouldBeGreaterThan(0);
        AssertGuardsIntact(memory);
        ReadRecord(slotted, slot).ShouldBe(Filled(180, 1));
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - SlottedPage: a relocating update that exactly fills the free space succeeds")]
    public void UpdateSlot_GrowIntoExactlyTheFreeSpace_ShouldRelocate()
    {
        // Arrange: leave exactly 300 free bytes after the slot directory entry.
        var memory = NewGuardedPage(out byte* page);
        var slotted = new SlottedPage(page);
        int slot = slotted.InsertSlot(new byte[100]);
        slotted.InsertSlot(new byte[slotted.FreeSpace - 300 - 4]);
        slotted.FreeSpace.ShouldBe(300);

        // Act
        slotted.UpdateSlot(slot, Filled(300, 9));

        // Assert
        slotted.FreeSpace.ShouldBe(0);
        ReadRecord(slotted, slot).ShouldBe(Filled(300, 9));
        AssertGeometry(slotted);
        AssertGuardsIntact(memory);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - SlottedPage: an insert into a page whose free-data end lies past the slot directory fails as corruption")]
    public void InsertSlot_FreeDataEndPastSlotDirectory_ShouldThrowCorruption()
    {
        // Arrange: the header state the unchecked relocation used to leave behind.
        var memory = NewGuardedPage(out byte* page);
        var slotted = new SlottedPage(page);
        slotted.InsertSlot(new byte[10]);
        ((ushort*)(page + 24))[0] = 8296;

        // Act
        var exception = Should.Throw<StorageCorruptionException>(() => slotted.InsertSlot(new byte[1]));

        // Assert
        exception.PageId.ShouldBe((PageId)pageId);
        AssertGuardsIntact(memory);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - SlottedPage: an in-place update of a slot outside the record area fails as corruption")]
    public void UpdateSlot_SlotOutsideRecordArea_ShouldThrowCorruption()
    {
        // Arrange: slot 0 claims 200 bytes starting 50 bytes before the page end.
        var memory = NewGuardedPage(out byte* page);
        var slotted = new SlottedPage(page);
        slotted.InsertSlot(new byte[200]);
        SetSlot(page, 0, offset: Page.Size - 50, length: 200);

        // Act / Assert
        Should.Throw<StorageCorruptionException>(() => slotted.UpdateSlot(0, new byte[150]));
        AssertGuardsIntact(memory);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - SlottedPage: reading a slot that runs past the page fails as corruption instead of reading past it")]
    public void ReadSlot_SlotPastPageEnd_ShouldThrowCorruption()
    {
        // Arrange: the guarded page stays referenced to the end; only the pointer into it is used,
        // and a collection of the pinned array would leave the pointer reading freed memory.
        var memory = NewGuardedPage(out byte* page);
        var slotted = new SlottedPage(page);
        slotted.InsertSlot(new byte[200]);
        SetSlot(page, 0, offset: Page.Size - 50, length: 200);

        // Act / Assert
        Should.Throw<StorageCorruptionException>(() => slotted.ReadSlot(0, new byte[200]));
        AssertGuardsIntact(memory);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - SlottedPage: a slot count no page can hold fails as corruption")]
    public void GetSlotLength_SlotCountBeyondPageCapacity_ShouldThrowCorruption()
    {
        // Arrange: slot 4000 would sit thousands of bytes before the buffer.
        var memory = NewGuardedPage(out byte* page);
        var slotted = new SlottedPage(page);
        slotted.Initialize();
        ((ushort*)(page + 22))[0] = 5000;

        // Act / Assert
        Should.Throw<StorageCorruptionException>(() => slotted.GetSlotLength(4000));
        Should.Throw<StorageCorruptionException>(() => slotted.DeleteSlot(4000));
        AssertGuardsIntact(memory);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - SlottedPage: compaction keeps a relocated record that sits above a later slot")]
    public void Compact_RelocatedRecordAboveLaterSlot_ShouldPreserveEveryRecord()
    {
        // Arrange: slot 0 relocates above slot 1, so offset order differs from slot order.
        var memory = NewGuardedPage(out byte* page);
        var slotted = new SlottedPage(page);
        int first = slotted.InsertSlot(Filled(100, 1));
        int second = slotted.InsertSlot(Filled(120, 2));
        slotted.UpdateSlot(first, Filled(160, 3));

        // Act
        slotted.Compact();

        // Assert: both records survive and the dead 100 bytes are reclaimed.
        ReadRecord(slotted, first).ShouldBe(Filled(160, 3));
        ReadRecord(slotted, second).ShouldBe(Filled(120, 2));
        ((int)slotted.FreeDataEnd).ShouldBe(SlottedPage.BodyOffset + 160 + 120);
        AssertGeometry(slotted);
        AssertGuardsIntact(memory);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - SlottedPage: compaction refuses overlapping records before moving any byte")]
    public void Compact_OverlappingRecords_ShouldThrowCorruptionWithoutMovingBytes()
    {
        // Arrange: slot 1 is pointed into the middle of slot 0's record.
        var memory = NewGuardedPage(out byte* page);
        var slotted = new SlottedPage(page);
        slotted.InsertSlot(Filled(100, 1));
        slotted.InsertSlot(Filled(100, 2));
        slotted.DeleteSlot(0);
        SetSlot(page, 0, offset: SlottedPage.BodyOffset + 150, length: 30);
        byte[] before = PageBytes(memory);

        // Act / Assert
        Should.Throw<StorageCorruptionException>(() => slotted.Compact());
        PageBytes(memory).ShouldBe(before);
    }

    /// <summary>
    /// A reader beside the page's writer (a scan takes a pin, not a latch) copies the slots below the
    /// slot count it reads, and every slot an insert or a relocating update published reads whole:
    /// never deleted, never outside the page body, never bytes that are not its record. Before ordered
    /// publication a relocating update marked the entry deleted and then rewrote it one field at a
    /// time, which a reader on any hardware can catch, and on ARM64 a reader could see an insert's
    /// new slot count ahead of its entry, which a SQL scan reported as a malformed page (#1362
    /// review).
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Storage] - SlottedPage: a reader beside inserts and relocating updates reads every published slot whole")]
    public void TryReadSlot_BesideInsertsAndRelocations_ShouldReadEveryPublishedSlotWhole()
    {
        // Arrange: the writer fills fresh pages, each kept alive until the reader stops, and hands
        // each to the reader before its first insert.
        var pages = new List<byte[]>();
        nint current = 0;
        bool reading = false;
        bool done = false;
        string? failure = null;
        long slotsRead = 0;
        var reader = new Thread(() =>
        {
            Volatile.Write(ref reading, true);
            while (!Volatile.Read(ref done) && failure is null)
            {
                var page = (byte*)Volatile.Read(ref current);
                if (page is null)
                {
                    continue;
                }

                var slotted = new SlottedPage(page);
                int count = slotted.SlotCount;
                for (int slot = 0; slot < count && failure is null; slot++)
                {
                    try
                    {
                        if (!slotted.TryReadSlot(slot, out byte[] record))
                        {
                            failure = $"slot {slot} of {count} read as deleted";
                        }
                        else if (record.Length == 0 || record.AsSpan().IndexOfAnyExcept(FillOf(slot)) >= 0)
                        {
                            failure = $"slot {slot} of {count} read {record.Length} bytes that are not its record";
                        }

                        slotsRead++;
                    }
                    catch (StorageCorruptionException exception)
                    {
                        failure = exception.Message;
                    }
                }
            }
        });

        // Act: inserts, each followed by a relocating update of the slot inserted two before, until
        // the reader has run beside 2048 pages and read 100,000 slots (at most 4096 pages or two
        // seconds).
        reader.Start();
        SpinWait.SpinUntil(() => Volatile.Read(ref reading), TimeSpan.FromSeconds(10)).ShouldBeTrue();
        var elapsed = Stopwatch.StartNew();
        while (Volatile.Read(ref failure) is null
            && pages.Count < 4096
            && elapsed.Elapsed < TimeSpan.FromSeconds(2)
            && (pages.Count < 2048 || Volatile.Read(ref slotsRead) < 100_000))
        {
            var memory = GC.AllocateArray<byte>(Page.Size, pinned: true);
            var page = (byte*)Marshal.UnsafeAddrOfPinnedArrayElement(memory, 0);
            var header = new Page(page);
            header.Id = pageId;
            var slotted = new SlottedPage(page);
            slotted.Initialize();
            pages.Add(memory);
            Volatile.Write(ref current, (nint)page);

            var lengths = new List<int>();
            while (slotted.CanFit(24 + (lengths.Count % 5 * 8)))
            {
                int slot = lengths.Count;
                lengths.Add(24 + (slot % 5 * 8));
                slotted.InsertSlot(Filled(lengths[slot], FillOf(slot)));

                int earlier = slot - 2;
                if (earlier >= 0 && lengths[earlier] + 8 <= slotted.FreeSpace)
                {
                    lengths[earlier] += 8;
                    slotted.UpdateSlot(earlier, Filled(lengths[earlier], FillOf(earlier)));
                }
            }
        }

        Volatile.Write(ref done, true);
        reader.Join();

        // Assert
        failure.ShouldBeNull();
        slotsRead.ShouldBeGreaterThan(0);
        GC.KeepAlive(pages);
    }

    private static byte FillOf(int slot) => (byte)((slot % 255) + 1);

    private static byte[] NewGuardedPage(out byte* page)
    {
        // A pinned-heap array never moves, so the pointer stays valid for the test.
        var memory = GC.AllocateArray<byte>(guardSize + Page.Size + guardSize, pinned: true);
        memory.AsSpan().Fill(guardByte);
        memory.AsSpan(guardSize, Page.Size).Clear();
        page = (byte*)Marshal.UnsafeAddrOfPinnedArrayElement(memory, guardSize);
        var header = new Page(page);
        header.Id = pageId;
        new SlottedPage(page).Initialize();
        return memory;
    }

    private static void SetSlot(byte* page, int index, int offset, int length)
    {
        var slot = (PageSlot*)(page + Page.Size - ((index + 1) * sizeof(PageSlot)));
        slot->Offset = (ushort)offset;
        slot->Length = (ushort)length;
    }

    private static byte[] PageBytes(byte[] memory) => memory.AsSpan(guardSize, Page.Size).ToArray();

    private static byte[] ReadRecord(SlottedPage slotted, int slot)
    {
        var record = new byte[slotted.GetSlotLength(slot)];
        slotted.ReadSlot(slot, record);
        return record;
    }

    private static byte[] Filled(int length, byte value) => Enumerable.Repeat(value, length).ToArray();

    private static void AssertGeometry(SlottedPage slotted)
    {
        ((int)slotted.FreeDataEnd).ShouldBeGreaterThanOrEqualTo(SlottedPage.BodyOffset);
        ((int)slotted.FreeDataEnd).ShouldBeLessThanOrEqualTo(Page.Size - (slotted.SlotCount * sizeof(PageSlot)));
        slotted.FreeSpace.ShouldBeGreaterThanOrEqualTo(0);
    }

    private static void AssertGuardsIntact(byte[] memory)
    {
        memory.AsSpan(0, guardSize).IndexOfAnyExcept(guardByte).ShouldBe(-1, "the guard before the page");
        memory.AsSpan(guardSize + Page.Size).IndexOfAnyExcept(guardByte).ShouldBe(-1, "the guard after the page");
    }
}
