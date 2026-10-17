using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Assimalign.Cohesion.Database.Storage.Units;

/// <summary>
/// Provides managed operations over a <see cref="Page"/> using the slotted page format.
/// Records grow forward from the body start, while the slot directory grows backward
/// from the end of the page, with free space in the middle.
/// </summary>
/// <remarks>
/// <para>
/// This layout is universal across all database models (SQL, Document, KeyValuePair, Graph).
/// Each model stores different record types, but the physical page layout with the slot
/// directory mechanism is identical.
/// </para>
/// <para>
/// Every operation checks the page geometry before it dereferences an offset taken from
/// the page: a write never lands outside the page's data area, and a read never leaves
/// the page body. The page is native memory (the buffer pool hands out raw pointers into
/// pinned buffers), so an unchecked offset is a write into whatever object the allocator
/// placed next to the buffer — the failure behind #1157. A page whose header or slot
/// directory is out of range fails with <see cref="StorageCorruptionException"/>.
/// </para>
/// <para>
/// Reads run beside the page's single writer (a reader takes a pin, not a latch), so the writer
/// publishes in order: a record's bytes, then its slot entry as one 32-bit release write, then,
/// for a new slot, the slot count as a release write. A reader loads the slot count and the
/// entry with acquire reads, the entry as one 32-bit load, so on weakly ordered hardware such as
/// ARM64 it never sees a slot count ahead of its entry, half an entry, or an entry ahead of its
/// record's bytes.
/// </para>
/// <code>
/// ┌──────────────────────────────────────┐  Offset 0
/// │ Page Header (96 bytes)               │
/// ├──────────────────────────────────────┤  Offset 96 (BodyOffset)
/// │ Record 0 data                        │
/// │ Record 1 data                        │
/// │ Record 2 data                        │
/// │ ...                                  │
/// │                                      │
/// │ ── Free Space ──                     │
/// │                                      │
/// │ [Slot 2] [Slot 1] [Slot 0]          │  ← Grows backward from page end
/// └──────────────────────────────────────┘  Offset 8191
/// </code>
/// </remarks>
public readonly unsafe struct SlottedPage
{
    private readonly byte* _pointer;

    /// <summary>
    /// The byte offset where the page body begins (immediately after the page header).
    /// </summary>
    public const int BodyOffset = Page.HeaderSize;

    /// <summary>
    /// The usable body size in bytes (total page size minus header size).
    /// </summary>
    public const int BodySize = Page.Size - Page.HeaderSize;

    /// <summary>
    /// The largest record that fits in a single page (the body minus one slot entry).
    /// </summary>
    public const int MaxRecordSize = BodySize - 4;

    // The slot directory grows backward from the page end and may reach the body start,
    // never the header: more slots than this cannot exist on a well-formed page.
    private const int maxSlotCount = BodySize / 4;

    /// <summary>
    /// Initializes a new <see cref="SlottedPage"/> over the given page buffer.
    /// </summary>
    /// <param name="page">The underlying page whose buffer will be managed.</param>
    public SlottedPage(Page page)
    {
        _pointer = page.Pointer;
    }

    /// <summary>
    /// Initializes a new <see cref="SlottedPage"/> over a raw buffer pointer.
    /// </summary>
    /// <param name="pointer">A pointer to the start of an 8KB page buffer.</param>
    public SlottedPage(byte* pointer)
    {
        _pointer = pointer;
    }

    /// <summary>
    /// Gets the number of slots (records) in this page.
    /// </summary>
    /// <remarks>
    /// A plain read, for the page's writer and for a loop bound. A reader beside the writer reads
    /// a slot through <see cref="ReadSlot"/> or <see cref="TryReadSlot"/>, which read the count
    /// again with acquire semantics before they read the slot.
    /// </remarks>
    public ushort SlotCount
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ((Page.Header*)_pointer)->SlotCount;
    }

    /// <summary>
    /// Gets the byte offset (from page start) where the next record would be written.
    /// </summary>
    public ushort FreeDataEnd
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ((Page.Header*)_pointer)->FreeDataEnd;
    }

    /// <summary>
    /// Gets the number of free bytes available for new records and slots.
    /// </summary>
    public int FreeSpace
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            int slotArrayStart = Page.Size - (SlotCount * sizeof(PageSlot));
            return slotArrayStart - FreeDataEnd;
        }
    }

    /// <summary>
    /// Determines whether a record of the given size can fit in the page,
    /// accounting for both the record data and the new slot entry.
    /// </summary>
    /// <param name="recordSize">The size of the record in bytes.</param>
    /// <returns><c>true</c> if the record can fit; otherwise, <c>false</c>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool CanFit(int recordSize)
    {
        return recordSize + sizeof(PageSlot) <= FreeSpace;
    }

    /// <summary>
    /// Reads a record at the specified slot index into the destination buffer.
    /// </summary>
    /// <param name="index">The zero-based slot index.</param>
    /// <param name="destination">The buffer to copy the record data into.</param>
    /// <returns>The number of bytes copied.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The slot index is out of range.</exception>
    /// <exception cref="StorageException">The slot has been deleted.</exception>
    /// <exception cref="StorageCorruptionException">The slot describes a record outside the page body.</exception>
    public int ReadSlot(int index, Span<byte> destination)
    {
        // One snapshot of the entry: the bounds check and the copy use the same values.
        PageSlot slot = ReadEntry(GetSlotPtr(index));

        if (slot.IsDeleted)
        {
            throw new SlottedPageException("Cannot read a deleted slot.");
        }

        // Reads may run beside the page's single writer (scans take a pin, not a latch),
        // so only the body bound is enforced here: it holds in every state a well-formed
        // page passes through, and it is what keeps a read inside the buffer.
        if (slot.Offset < BodyOffset || slot.Offset + slot.Length > Page.Size)
        {
            throw Corruption($"slot {index} addresses bytes {slot.Offset}..{slot.Offset + slot.Length}, outside the page body");
        }

        int length = Math.Min(slot.Length, destination.Length);
        new ReadOnlySpan<byte>(_pointer + slot.Offset, length).CopyTo(destination);
        return length;
    }

    /// <summary>
    /// Copies the record at the specified slot when the slot still holds one. An index past
    /// the slot directory (a bracket rollback restored a pre-image with fewer slots) and a
    /// deleted slot are the two ways a slot stops holding a record, and both return
    /// <c>false</c>; a malformed page throws, as <see cref="ReadSlot"/> does.
    /// </summary>
    /// <param name="index">The zero-based slot index.</param>
    /// <param name="record">A copy of the record, or an empty array when the method returns <c>false</c>.</param>
    /// <returns><c>true</c> when the slot holds a record; otherwise, <c>false</c>.</returns>
    /// <exception cref="StorageCorruptionException">
    /// The page header records more slots than a page can hold, or the slot describes a record outside the page body.
    /// </exception>
    internal bool TryReadSlot(int index, out byte[] record)
    {
        int slotCount = ReadPublishedSlotCount();

        if ((uint)index >= (uint)slotCount)
        {
            record = [];
            return false;
        }

        if (slotCount > maxSlotCount)
        {
            throw Corruption($"the header records {slotCount} slots; a page holds at most {maxSlotCount}");
        }

        // One snapshot of the entry, as in ReadSlot: the checks and the copy use the same values.
        // The count and the entry are acquire reads, so the entry is never older than the count
        // that admitted it, nor half written: before ordered publication, a scan on ARM64 read
        // an insert's new count with the entry's offset still zero and reported a healthy page as
        // malformed (#1362 review).
        PageSlot slot = ReadEntry(SlotAt(index));

        if (slot.IsDeleted)
        {
            record = [];
            return false;
        }

        if (slot.Offset < BodyOffset || slot.Offset + slot.Length > Page.Size)
        {
            throw Corruption($"slot {index} addresses bytes {slot.Offset}..{slot.Offset + slot.Length}, outside the page body");
        }

        record = new ReadOnlySpan<byte>(_pointer + slot.Offset, slot.Length).ToArray();
        return true;
    }

    /// <summary>
    /// Gets the length of the record at the specified slot index.
    /// </summary>
    /// <param name="index">The zero-based slot index.</param>
    /// <returns>The length of the record in bytes, or zero if the slot is deleted.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The slot index is out of range.</exception>
    /// <exception cref="StorageCorruptionException">The page header records more slots than a page can hold.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetSlotLength(int index)
    {
        return GetSlotPtr(index)->Length;
    }

    /// <summary>
    /// Inserts a new record into the page. The record data is written at the current
    /// free data position and a new slot is appended to the slot directory.
    /// </summary>
    /// <param name="data">The record data to insert.</param>
    /// <returns>The zero-based slot index of the newly inserted record.</returns>
    /// <exception cref="StorageException">The page does not have enough free space.</exception>
    /// <exception cref="StorageCorruptionException">The page header describes free space outside the page's data area.</exception>
    public int InsertSlot(ReadOnlySpan<byte> data)
    {
        var header = (Page.Header*)_pointer;
        int slotIndex = header->SlotCount;
        int recordOffset = header->FreeDataEnd;
        EnsureWritableLayout(slotIndex, recordOffset);

        // The record and its new slot entry both come out of the free space.
        if (data.Length + sizeof(PageSlot) > SlotDirectoryStart(slotIndex) - recordOffset)
        {
            throw new SlottedPageException("Insufficient free space in page for the record.");
        }

        // Write record data at FreeDataEnd
        data.CopyTo(new Span<byte>(_pointer + recordOffset, data.Length));

        // Advance FreeDataEnd
        header->FreeDataEnd = (ushort)(recordOffset + data.Length);

        // Publish the entry, then the count: a reader that sees the new count sees the entry and
        // the record bytes behind it.
        PublishEntry(SlotAt(slotIndex), recordOffset, data.Length);
        Volatile.Write(ref header->SlotCount, (ushort)(slotIndex + 1));

        return slotIndex;
    }

    /// <summary>
    /// Marks a slot as deleted. The record data is not immediately reclaimed;
    /// use <see cref="Compact"/> to defragment the page and recover space.
    /// </summary>
    /// <param name="index">The zero-based slot index to delete.</param>
    /// <exception cref="ArgumentOutOfRangeException">The slot index is out of range.</exception>
    /// <exception cref="StorageCorruptionException">The page header records more slots than a page can hold.</exception>
    public void DeleteSlot(int index)
    {
        var slot = GetSlotPtr(index);
        slot->Length = 0;
    }

    /// <summary>
    /// Updates an existing slot with new record data. If the new data fits in the
    /// existing slot space, it is written in place. Otherwise, the old slot is deleted
    /// and the record is appended at the end of the data area.
    /// </summary>
    /// <remarks>
    /// A relocated record leaves its old bytes behind as dead space that only
    /// <see cref="Compact"/> reclaims, so relocation needs the record's <b>whole</b> length
    /// in free space, not just its growth. The page is left unchanged when the record
    /// does not fit.
    /// </remarks>
    /// <param name="index">The zero-based slot index to update.</param>
    /// <param name="data">The new record data.</param>
    /// <exception cref="ArgumentOutOfRangeException">The slot index is out of range.</exception>
    /// <exception cref="StorageException">The page does not have enough free space for the updated record.</exception>
    /// <exception cref="StorageCorruptionException">The page header or the slot describes bytes outside the page's data area.</exception>
    public void UpdateSlot(int index, ReadOnlySpan<byte> data)
    {
        var header = (Page.Header*)_pointer;
        int slotCount = header->SlotCount;
        int freeDataEnd = header->FreeDataEnd;
        EnsureWritableLayout(slotCount, freeDataEnd);

        if ((uint)index >= (uint)slotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var slot = SlotAt(index);
        PageSlot current = *slot;

        if (!current.IsDeleted && (current.Offset < BodyOffset || current.Offset + current.Length > freeDataEnd))
        {
            throw Corruption($"slot {index} addresses bytes {current.Offset}..{current.Offset + current.Length}, outside the record area that ends at {freeDataEnd}");
        }

        // If the new data fits in the existing space, write in place
        if (data.Length <= current.Length)
        {
            data.CopyTo(new Span<byte>(_pointer + current.Offset, data.Length));
            PublishEntry(slot, current.Offset, data.Length);
            return;
        }

        // Relocation appends the complete record at the free-data end. Checking only
        // the growth (data.Length - current.Length) against the free space was #1157:
        // with less room than the record, the append ran through the slot directory and
        // past the end of the page into the buffer pool's own objects.
        if (data.Length > SlotDirectoryStart(slotCount) - freeDataEnd)
        {
            throw new SlottedPageException("Insufficient free space to update the record.");
        }

        // Append at the free-data end, then move the entry there in one write. The old bytes stay
        // as dead space, so a reader holding the old entry still copies the old record whole; the
        // entry used to be marked deleted first and rewritten a field at a time, so a reader beside
        // the update read a live record as deleted or paired the old offset with the new length.
        data.CopyTo(new Span<byte>(_pointer + freeDataEnd, data.Length));
        header->FreeDataEnd = (ushort)(freeDataEnd + data.Length);

        PublishEntry(slot, freeDataEnd, data.Length);
    }

    /// <summary>
    /// Compacts the page by defragmenting record data—moving all live records
    /// to the front of the data area and reclaiming space from deleted slots.
    /// Slot indices remain stable (deleted slots become empty entries).
    /// </summary>
    /// <remarks>
    /// Records move in ascending offset order, not slot order: a relocated record can sit
    /// above a record with a higher slot index, and moving in slot order would overwrite
    /// that record before it moved. Overlapping records are corruption and fail before any
    /// byte moves.
    /// </remarks>
    /// <exception cref="StorageCorruptionException">The page header or a live slot describes bytes outside the page's data area, or two live records overlap.</exception>
    public void Compact()
    {
        var header = (Page.Header*)_pointer;
        int slotCount = header->SlotCount;
        int freeDataEnd = header->FreeDataEnd;
        EnsureWritableLayout(slotCount, freeDataEnd);

        // Sort keys pack (offset, slot index) so an ordinary integer sort orders live
        // records by offset; an offset below 2^13 and an index below 2^11 fit an int.
        Span<int> order = slotCount <= 256 ? stackalloc int[slotCount] : new int[slotCount];
        int liveCount = 0;

        for (int i = 0; i < slotCount; i++)
        {
            PageSlot slot = *SlotAt(i);

            if (slot.IsDeleted)
            {
                continue;
            }

            if (slot.Offset < BodyOffset || slot.Offset + slot.Length > freeDataEnd)
            {
                throw Corruption($"slot {i} addresses bytes {slot.Offset}..{slot.Offset + slot.Length}, outside the record area that ends at {freeDataEnd}");
            }

            order[liveCount++] = (slot.Offset << 16) | i;
        }

        order = order[..liveCount];
        order.Sort();

        int previousEnd = BodyOffset;
        foreach (int key in order)
        {
            var slot = SlotAt(key & 0xFFFF);

            if (slot->Offset < previousEnd)
            {
                throw Corruption($"slot {key & 0xFFFF} overlaps the record that ends at {previousEnd}");
            }

            previousEnd = slot->Offset + slot->Length;
        }

        int writeOffset = BodyOffset;
        foreach (int key in order)
        {
            var slot = SlotAt(key & 0xFFFF);

            if (slot->Offset != writeOffset)
            {
                // Records only move toward the body start; MemoryCopy handles the overlap.
                Buffer.MemoryCopy(
                    _pointer + slot->Offset,
                    _pointer + writeOffset,
                    slot->Length,
                    slot->Length);
                slot->Offset = (ushort)writeOffset;
            }

            writeOffset += slot->Length;
        }

        header->FreeDataEnd = (ushort)writeOffset;
    }

    /// <summary>
    /// Initializes a fresh page for slotted page use by setting the initial
    /// free data boundary to the start of the body and zeroing the slot count.
    /// </summary>
    public void Initialize()
    {
        var header = (Page.Header*)_pointer;
        header->SlotCount = 0;
        header->FreeDataEnd = (ushort)BodyOffset;
    }

    /// <summary>
    /// Returns the entry for an existing slot, rejecting an index outside the recorded
    /// slot count and a slot count no page can hold — the latter would place the entry
    /// in the header or before the buffer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private PageSlot* GetSlotPtr(int index)
    {
        int slotCount = ReadPublishedSlotCount();

        if ((uint)index >= (uint)slotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        if (slotCount > maxSlotCount)
        {
            throw Corruption($"the header records {slotCount} slots; a page holds at most {maxSlotCount}");
        }

        return SlotAt(index);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private PageSlot* SlotAt(int index)
    {
        // Slots are stored at the end of the page, growing backward.
        // Slot 0 is at the very last position, Slot 1 is before it, etc.
        return (PageSlot*)(_pointer + Page.Size - ((index + 1) * sizeof(PageSlot)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SlotDirectoryStart(int slotCount) => Page.Size - (slotCount * sizeof(PageSlot));

    /// <summary>
    /// Reads the slot count with acquire semantics: every slot below it has its entry and its
    /// record's bytes published (<see cref="InsertSlot"/>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ReadPublishedSlotCount() => Volatile.Read(ref ((Page.Header*)_pointer)->SlotCount);

    /// <summary>
    /// Reads a slot entry as one 32-bit acquire load, so the offset and the length come from the
    /// same write and the record bytes it addresses are at least as new as the entry. The
    /// directory sits at the end of a pool buffer, which is 8-byte aligned, so every entry is
    /// 4-byte aligned, as an acquire load on ARM64 requires.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static PageSlot ReadEntry(PageSlot* entry)
    {
        uint raw = Volatile.Read(ref *(uint*)entry);
        return Unsafe.As<uint, PageSlot>(ref raw);
    }

    /// <summary>
    /// Writes a slot entry as one 32-bit release store, after every byte of the record it
    /// addresses (<see cref="ReadEntry"/>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PublishEntry(PageSlot* entry, int offset, int length)
    {
        var slot = new PageSlot { Offset = (ushort)offset, Length = (ushort)length };
        Volatile.Write(ref *(uint*)entry, Unsafe.As<PageSlot, uint>(ref slot));
    }

    /// <summary>
    /// Checks the header geometry a write relies on: the slot directory fits the body
    /// and the free-data end lies between the body start and the slot directory. A
    /// writer owns the page (page write locks), so the header it reads is stable.
    /// </summary>
    private void EnsureWritableLayout(int slotCount, int freeDataEnd)
    {
        if (slotCount > maxSlotCount)
        {
            throw Corruption($"the header records {slotCount} slots; a page holds at most {maxSlotCount}");
        }

        if (freeDataEnd < BodyOffset || freeDataEnd > SlotDirectoryStart(slotCount))
        {
            throw Corruption($"the free-data end {freeDataEnd} lies outside the record area {BodyOffset}..{SlotDirectoryStart(slotCount)}");
        }
    }

    private StorageCorruptionException Corruption(string detail)
    {
        long pageId = ((Page.Header*)_pointer)->PageId;
        return new StorageCorruptionException((PageId)pageId, $"Slotted page {pageId} is malformed: {detail}.");
    }
}
