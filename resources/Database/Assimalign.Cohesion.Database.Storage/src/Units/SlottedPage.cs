using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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
    public int ReadSlot(int index, Span<byte> destination)
    {
        if ((uint)index >= SlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var slot = GetSlotPtr(index);

        if (slot->IsDeleted)
        {
            throw new SlottedPageException("Cannot read a deleted slot.");
        }

        int length = Math.Min(slot->Length, destination.Length);
        new ReadOnlySpan<byte>(_pointer + slot->Offset, length).CopyTo(destination);
        return length;
    }

    /// <summary>
    /// Gets the length of the record at the specified slot index.
    /// </summary>
    /// <param name="index">The zero-based slot index.</param>
    /// <returns>The length of the record in bytes, or zero if the slot is deleted.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The slot index is out of range.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetSlotLength(int index)
    {
        if ((uint)index >= SlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return GetSlotPtr(index)->Length;
    }

    /// <summary>
    /// Inserts a new record into the page. The record data is written at the current
    /// free data position and a new slot is appended to the slot directory.
    /// </summary>
    /// <param name="data">The record data to insert.</param>
    /// <returns>The zero-based slot index of the newly inserted record.</returns>
    /// <exception cref="StorageException">The page does not have enough free space.</exception>
    public int InsertSlot(ReadOnlySpan<byte> data)
    {
        if (!CanFit(data.Length))
        {
            throw new SlottedPageException("Insufficient free space in page for the record.");
        }

        var header = (Page.Header*)_pointer;

        // Write record data at FreeDataEnd
        ushort recordOffset = header->FreeDataEnd;
        data.CopyTo(new Span<byte>(_pointer + recordOffset, data.Length));

        // Advance FreeDataEnd
        header->FreeDataEnd = (ushort)(recordOffset + data.Length);

        // Write new slot entry at the end of the slot directory
        int slotIndex = header->SlotCount;
        var slotPtr = GetSlotPtr(slotIndex);
        slotPtr->Offset = recordOffset;
        slotPtr->Length = (ushort)data.Length;

        // Increment slot count
        header->SlotCount = (ushort)(slotIndex + 1);

        return slotIndex;
    }

    /// <summary>
    /// Marks a slot as deleted. The record data is not immediately reclaimed;
    /// use <see cref="Compact"/> to defragment the page and recover space.
    /// </summary>
    /// <param name="index">The zero-based slot index to delete.</param>
    /// <exception cref="ArgumentOutOfRangeException">The slot index is out of range.</exception>
    public void DeleteSlot(int index)
    {
        if ((uint)index >= SlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var slot = GetSlotPtr(index);
        slot->Length = 0;
    }

    /// <summary>
    /// Updates an existing slot with new record data. If the new data fits in the
    /// existing slot space, it is written in place. Otherwise, the old slot is deleted
    /// and the record is appended at the end of the data area.
    /// </summary>
    /// <param name="index">The zero-based slot index to update.</param>
    /// <param name="data">The new record data.</param>
    /// <exception cref="ArgumentOutOfRangeException">The slot index is out of range.</exception>
    /// <exception cref="StorageException">The page does not have enough free space for the updated record.</exception>
    public void UpdateSlot(int index, ReadOnlySpan<byte> data)
    {
        if ((uint)index >= SlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var slot = GetSlotPtr(index);

        // If the new data fits in the existing space, write in place
        if (data.Length <= slot->Length)
        {
            data.CopyTo(new Span<byte>(_pointer + slot->Offset, data.Length));
            slot->Length = (ushort)data.Length;
            return;
        }

        // Otherwise, need more space. Check if we can fit the difference.
        int additionalNeeded = data.Length - slot->Length;
        if (additionalNeeded > FreeSpace)
        {
            throw new SlottedPageException("Insufficient free space to update the record.");
        }

        // Mark old slot as deleted and append at end
        slot->Length = 0;

        var header = (Page.Header*)_pointer;
        ushort recordOffset = header->FreeDataEnd;
        data.CopyTo(new Span<byte>(_pointer + recordOffset, data.Length));
        header->FreeDataEnd = (ushort)(recordOffset + data.Length);

        slot->Offset = recordOffset;
        slot->Length = (ushort)data.Length;
    }

    /// <summary>
    /// Compacts the page by defragmenting record data—moving all live records
    /// to the front of the data area and reclaiming space from deleted slots.
    /// Slot indices remain stable (deleted slots become empty entries).
    /// </summary>
    public void Compact()
    {
        var header = (Page.Header*)_pointer;
        ushort writeOffset = (ushort)BodyOffset;
        int slotCount = header->SlotCount;

        for (int i = 0; i < slotCount; i++)
        {
            var slot = GetSlotPtr(i);

            if (slot->IsDeleted)
            {
                continue;
            }

            if (slot->Offset != writeOffset)
            {
                // Move record data to the compacted position  
                Buffer.MemoryCopy(
                    _pointer + slot->Offset,
                    _pointer + writeOffset,
                    slot->Length,
                    slot->Length);
                slot->Offset = writeOffset;
            }

            writeOffset += slot->Length;
        }

        header->FreeDataEnd = writeOffset;
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private PageSlot* GetSlotPtr(int index)
    {
        // Slots are stored at the end of the page, growing backward.
        // Slot 0 is at the very last position, Slot 1 is before it, etc.
        return (PageSlot*)(_pointer + Page.Size - ((index + 1) * sizeof(PageSlot)));
    }
}
