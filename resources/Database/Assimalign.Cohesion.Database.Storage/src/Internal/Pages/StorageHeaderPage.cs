using System;
using System.Buffers.Binary;
using System.Text;

namespace Assimalign.Cohesion.Database.Storage.Internal;

using Assimalign.Cohesion.Database.Storage.Units;

/// <summary>
/// The layout of page 0 (the identity block and the two alternating header slots) and of
/// the checkpoint anchor's overflow pages, with their composition and validation
/// (storage format 2, #1251). See <see cref="StorageFileHeader"/> for the page diagram.
/// </summary>
/// <remarks>
/// <para>
/// <b>Header slots.</b> Each slot is 3,584 bytes, seven 512-byte sectors that share no
/// sector with the other slot or the identity block, and each lies inside one 4 KiB block.
/// A slot write rewrites only its own bytes. Its layout: magic (0), CRC-32C over the slot
/// with the checksum field zero (4), generation (8), LSN floor (16), sequence floor (24),
/// page count (32), free page count (40), modified time (48), anchor count (56), inline
/// anchor count (60), anchor chain head (64), anchor chain page count (72), a copy of the
/// identity block (80, <see cref="StorageFileHeader.ByteSize"/> bytes), then the inline
/// anchor sequences as little-endian 64-bit values from offset 336.
/// </para>
/// <para>
/// <b>Why each slot copies the identity block.</b> Slot 0 shares its 4 KiB block with the
/// identity block. The torn-write model is old-or-new per 512-byte sector, but a drive with
/// 4 KiB physical sectors that emulates 512-byte ones (512e) rewrites a slot-0 write as a
/// read-modify-write of the whole physical sector, and power lost during it can leave the
/// identity block unreadable too. Each slot therefore carries everything open needs, as each of
/// Voron's header files does (<c>src/Voron/Impl/FileHeaders/HeaderAccessor.cs:71-86</c>): when
/// the identity block fails its checksum or its magic, open takes the identity from the newest
/// valid slot, and the next write to slot 0 rewrites the whole block.
/// </para>
/// <para>
/// <b>Anchor pages</b> (<see cref="PageType.CheckpointAnchor"/>) carry the anchor sequences
/// that do not fit the slot. Each slot owns its own chain, so writing one slot never touches
/// the pages the other slot's generation reads. The body starts with the generation that
/// wrote the page (96), the slot (104), the entry count (108), the next page or zero (112)
/// and the page's position on the chain (120); the entries follow from offset 128. The page
/// carries the ordinary page checksum.
/// </para>
/// </remarks>
internal static class StorageHeaderPage
{
    /// <summary>The offset of <see cref="StorageFileHeader"/> in page 0.</summary>
    internal const int IdentityOffset = Page.HeaderSize;

    /// <summary>The offset of <see cref="StorageFileHeader.Magic"/> in page 0, the same in every storage format.</summary>
    internal const int MagicOffset = IdentityOffset;

    /// <summary>The offset of <see cref="StorageFileHeader.FormatVersion"/> in page 0, the same in every storage format.</summary>
    internal const int FormatVersionOffset = IdentityOffset + sizeof(int);

    // Offsets inside the identity block (StorageFileHeader's field offsets).
    private const int blockMagicOffset = 0;
    private const int blockFormatVersionOffset = 4;
    private const int blockPageSizeOffset = 8;
    private const int blockModelOffset = 12;
    private const int blockStorageIdOffset = 16;
    private const int blockCreatedOffset = 32;
    private const int blockNameOffset = 40;
    private const int blockChecksumOffset = 168;
    private const int nameCapacity = 128;

    /// <summary>The size of one header slot.</summary>
    internal const int SlotSize = 3584;

    /// <summary>The offset of header slot 0 in page 0.</summary>
    internal const int Slot0Offset = 512;

    /// <summary>The offset of header slot 1 in page 0.</summary>
    internal const int Slot1Offset = 4608;

    /// <summary>
    /// The size of page 0's leading 4 KiB block — the page header, the identity block and slot
    /// 0 — which a write to slot 0 rewrites whole when the identity block needs restoring.
    /// </summary>
    internal const int LeadingBlockSize = Slot0Offset + SlotSize;

    private const int slotMagicValue = 0x544F4C53; // "SLOT"
    private const int slotMagicOffset = 0;
    private const int slotChecksumOffset = 4;
    private const int slotGenerationOffset = 8;
    private const int slotLsnFloorOffset = 16;
    private const int slotSequenceFloorOffset = 24;
    private const int slotTotalPagesOffset = 32;
    private const int slotFreePagesOffset = 40;
    private const int slotModifiedOffset = 48;
    private const int slotAnchorCountOffset = 56;
    private const int slotAnchorInlineOffset = 60;
    private const int slotAnchorChainHeadOffset = 64;
    private const int slotAnchorChainPagesOffset = 72;
    private const int slotIdentityOffset = 80;
    private const int slotHeaderSize = slotIdentityOffset + StorageFileHeader.ByteSize;

    /// <summary>The most anchor sequences a slot holds itself.</summary>
    internal const int InlineAnchorCapacity = (SlotSize - slotHeaderSize) / sizeof(long);

    private const int anchorGenerationOffset = Page.HeaderSize;
    private const int anchorSlotOffset = Page.HeaderSize + 8;
    private const int anchorCountOffset = Page.HeaderSize + 12;
    private const int anchorNextOffset = Page.HeaderSize + 16;
    private const int anchorIndexOffset = Page.HeaderSize + 24;
    private const int anchorEntriesOffset = Page.HeaderSize + 32;

    /// <summary>The most anchor sequences one anchor page holds.</summary>
    internal const int AnchorPageCapacity = (Page.Size - anchorEntriesOffset) / sizeof(long);

    /// <summary>Gets the offset of a header slot in page 0.</summary>
    internal static int SlotOffset(int slot) => slot == 0 ? Slot0Offset : Slot1Offset;

    /// <summary>
    /// Composes a new identity block (<see cref="StorageFileHeader"/>) with its checksum.
    /// </summary>
    /// <param name="block">The <see cref="StorageFileHeader.ByteSize"/> bytes to compose.</param>
    internal static void ComposeIdentity(Span<byte> block, StorageId id, Name name, StorageModel model, long createdAtUtcTicks)
    {
        block = block[..StorageFileHeader.ByteSize];
        block.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(block[blockMagicOffset..], StorageFileHeader.ExpectedMagic);
        BinaryPrimitives.WriteInt32LittleEndian(block[blockFormatVersionOffset..], StorageFileHeader.CurrentFormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(block[blockPageSizeOffset..], Page.Size);
        BinaryPrimitives.WriteInt32LittleEndian(block[blockModelOffset..], (int)model);
        ((Guid)id).TryWriteBytes(block.Slice(blockStorageIdOffset, 16));
        BinaryPrimitives.WriteInt64LittleEndian(block[blockCreatedOffset..], createdAtUtcTicks);

        string? text = (string?)name;
        if (!string.IsNullOrEmpty(text))
        {
            byte[] encoded = Encoding.UTF8.GetBytes(text);
            encoded.AsSpan(0, Math.Min(encoded.Length, nameCapacity)).CopyTo(block[blockNameOffset..]);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(block[blockChecksumOffset..], ComputeIdentityChecksum(block));
    }

    /// <summary>
    /// Composes the start of page 0 up to slot 0: the page header (never checksummed: page 0
    /// never passes through the buffer pool), the identity block, and zeros up to slot 0.
    /// </summary>
    /// <param name="page0">At least <see cref="Slot0Offset"/> bytes of page 0.</param>
    /// <param name="identity">The identity block.</param>
    internal static void ComposePageStart(Span<byte> page0, ReadOnlySpan<byte> identity)
    {
        page0[..Slot0Offset].Clear();
        BinaryPrimitives.WriteInt64LittleEndian(page0, 0L); // page id
        page0[Page.TypeFieldOffset] = (byte)PageType.FileHeader;
        identity[..StorageFileHeader.ByteSize].CopyTo(page0[IdentityOffset..]);
    }

    /// <summary>
    /// Gets the identity block of page 0.
    /// </summary>
    internal static ReadOnlySpan<byte> IdentityBlock(ReadOnlySpan<byte> page0)
        => page0.Slice(IdentityOffset, StorageFileHeader.ByteSize);

    /// <summary>
    /// Reads the magic number and format version from raw page-0 bytes, at the offsets every
    /// storage format shares, before anything is verified.
    /// </summary>
    internal static (int Magic, int FormatVersion) ReadFormat(ReadOnlySpan<byte> page0)
        => ReadIdentityFormat(IdentityBlock(page0));

    /// <summary>
    /// Reads the magic number and format version of an identity block, before anything is
    /// verified.
    /// </summary>
    internal static (int Magic, int FormatVersion) ReadIdentityFormat(ReadOnlySpan<byte> identity)
        => (BinaryPrimitives.ReadInt32LittleEndian(identity[blockMagicOffset..]),
            BinaryPrimitives.ReadInt32LittleEndian(identity[blockFormatVersionOffset..]));

    /// <summary>
    /// Verifies an identity block's checksum.
    /// </summary>
    internal static bool VerifyIdentity(ReadOnlySpan<byte> identity)
        => BinaryPrimitives.ReadUInt32LittleEndian(identity[blockChecksumOffset..]) == ComputeIdentityChecksum(identity);

    /// <summary>
    /// Reads the storage identifier and name from a verified identity block.
    /// </summary>
    internal static (StorageId Id, Name Name) ReadIdentity(ReadOnlySpan<byte> identity)
    {
        var id = (StorageId)new Guid(identity.Slice(blockStorageIdOffset, 16));
        var nameBytes = identity.Slice(blockNameOffset, nameCapacity);
        int length = nameBytes.IndexOf((byte)0);
        if (length < 0)
        {
            length = nameCapacity;
        }

        var name = length > 0 ? (Name)Encoding.UTF8.GetString(nameBytes[..length]) : (Name)"";
        return (id, name);
    }

    /// <summary>
    /// Gets the copy of the identity block a verified header slot carries.
    /// </summary>
    internal static ReadOnlySpan<byte> SlotIdentity(ReadOnlySpan<byte> slot)
        => slot.Slice(slotIdentityOffset, StorageFileHeader.ByteSize);

    /// <summary>
    /// Composes a header slot: its fields, a copy of the identity block, its inline anchor
    /// sequences, zeros after them, and its checksum.
    /// </summary>
    internal static void WriteSlot(Span<byte> slot, StorageHeaderSlot state, ReadOnlySpan<byte> identity, ReadOnlySpan<long> inlineAnchor)
    {
        if (inlineAnchor.Length != state.AnchorInlineCount || inlineAnchor.Length > InlineAnchorCapacity)
        {
            throw new ArgumentException(ErrorMessages.StorageHeaderPageWriteSlotArgument, nameof(inlineAnchor));
        }

        if (identity.Length != StorageFileHeader.ByteSize)
        {
            throw new ArgumentException(ErrorMessages.StorageHeaderPageWriteSlotArgument2, nameof(identity));
        }

        slot = slot[..SlotSize];
        slot.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(slot[slotMagicOffset..], slotMagicValue);
        BinaryPrimitives.WriteInt64LittleEndian(slot[slotGenerationOffset..], state.Generation);
        BinaryPrimitives.WriteInt64LittleEndian(slot[slotLsnFloorOffset..], state.LsnFloor);
        BinaryPrimitives.WriteInt64LittleEndian(slot[slotSequenceFloorOffset..], state.SequenceFloor);
        BinaryPrimitives.WriteInt64LittleEndian(slot[slotTotalPagesOffset..], state.TotalPageCount);
        BinaryPrimitives.WriteInt64LittleEndian(slot[slotFreePagesOffset..], state.FreePageCount);
        BinaryPrimitives.WriteInt64LittleEndian(slot[slotModifiedOffset..], state.ModifiedAtUtcTicks);
        BinaryPrimitives.WriteInt32LittleEndian(slot[slotAnchorCountOffset..], state.AnchorCount);
        BinaryPrimitives.WriteInt32LittleEndian(slot[slotAnchorInlineOffset..], state.AnchorInlineCount);
        BinaryPrimitives.WriteInt64LittleEndian(slot[slotAnchorChainHeadOffset..], state.AnchorChainHead);
        BinaryPrimitives.WriteInt32LittleEndian(slot[slotAnchorChainPagesOffset..], state.AnchorChainPageCount);
        identity.CopyTo(slot[slotIdentityOffset..]);

        for (int i = 0; i < inlineAnchor.Length; i++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(slot[(slotHeaderSize + i * sizeof(long))..], inlineAnchor[i]);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(slot[slotChecksumOffset..], ComputeSlotChecksum(slot));
    }

    /// <summary>
    /// Validates a header slot: its magic number, its checksum, a positive generation and an
    /// anchor description that is consistent with the slot's and the pages' capacities. A slot
    /// that was never written, or whose write a crash tore, fails.
    /// </summary>
    internal static bool TryReadSlot(ReadOnlySpan<byte> slot, out StorageHeaderSlot? state)
    {
        state = null;
        slot = slot[..SlotSize];

        if (BinaryPrimitives.ReadInt32LittleEndian(slot[slotMagicOffset..]) != slotMagicValue
            || BinaryPrimitives.ReadUInt32LittleEndian(slot[slotChecksumOffset..]) != ComputeSlotChecksum(slot))
        {
            return false;
        }

        var candidate = new StorageHeaderSlot(
            Generation: BinaryPrimitives.ReadInt64LittleEndian(slot[slotGenerationOffset..]),
            LsnFloor: BinaryPrimitives.ReadInt64LittleEndian(slot[slotLsnFloorOffset..]),
            SequenceFloor: BinaryPrimitives.ReadInt64LittleEndian(slot[slotSequenceFloorOffset..]),
            TotalPageCount: BinaryPrimitives.ReadInt64LittleEndian(slot[slotTotalPagesOffset..]),
            FreePageCount: BinaryPrimitives.ReadInt64LittleEndian(slot[slotFreePagesOffset..]),
            ModifiedAtUtcTicks: BinaryPrimitives.ReadInt64LittleEndian(slot[slotModifiedOffset..]),
            AnchorCount: BinaryPrimitives.ReadInt32LittleEndian(slot[slotAnchorCountOffset..]),
            AnchorInlineCount: BinaryPrimitives.ReadInt32LittleEndian(slot[slotAnchorInlineOffset..]),
            AnchorChainHead: BinaryPrimitives.ReadInt64LittleEndian(slot[slotAnchorChainHeadOffset..]),
            AnchorChainPageCount: BinaryPrimitives.ReadInt32LittleEndian(slot[slotAnchorChainPagesOffset..]));

        int overflow = candidate.AnchorCount - candidate.AnchorInlineCount;
        bool consistent = candidate.Generation > 0
            && candidate.LsnFloor >= 0
            && candidate.SequenceFloor >= 0
            && candidate.AnchorInlineCount >= 0
            && candidate.AnchorInlineCount <= InlineAnchorCapacity
            && overflow >= 0
            && candidate.AnchorChainPageCount == (overflow + AnchorPageCapacity - 1) / AnchorPageCapacity
            && (overflow == 0 ? candidate.AnchorChainHead == 0 : candidate.AnchorChainHead > 0)
            && (overflow == 0 || candidate.AnchorInlineCount == InlineAnchorCapacity);

        if (!consistent)
        {
            return false;
        }

        state = candidate;
        return true;
    }

    /// <summary>
    /// Reads a verified slot's inline anchor sequences.
    /// </summary>
    internal static void ReadInlineAnchor(ReadOnlySpan<byte> slot, Span<long> destination)
    {
        for (int i = 0; i < destination.Length; i++)
        {
            destination[i] = BinaryPrimitives.ReadInt64LittleEndian(slot[(slotHeaderSize + i * sizeof(long))..]);
        }
    }

    /// <summary>
    /// Composes an anchor page in a page buffer: the page header (the pool stamps the
    /// checksum when it writes the page back) and the chain fields and entries.
    /// </summary>
    /// <param name="page">The page buffer.</param>
    /// <param name="pageId">The page's identifier.</param>
    /// <param name="lsn">
    /// The page LSN: the journal's last LSN once the chain's pages were allocated. The page
    /// is written outside the journal, and may reuse a page whose free is still in a journal
    /// tail that is not durable; the LSN makes the buffer pool's write-ahead gate flush that
    /// tail before any write-back of the page.
    /// </param>
    /// <param name="generation">The header generation the chain belongs to.</param>
    /// <param name="slot">The header slot that owns the chain.</param>
    /// <param name="index">The page's position on the chain.</param>
    /// <param name="entries">The anchor sequences the page holds.</param>
    /// <param name="next">The next page of the chain, or zero.</param>
    internal static void WriteAnchorPage(Span<byte> page, long pageId, long lsn, long generation, int slot, int index, ReadOnlySpan<long> entries, long next)
    {
        if (entries.Length is 0 or > AnchorPageCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(entries), entries.Length, ErrorMessages.HeaderPageWriteAnchorPageFailure);
        }

        page = page[..Page.Size];
        page.Clear();
        BinaryPrimitives.WriteInt64LittleEndian(page, pageId);
        BinaryPrimitives.WriteInt64LittleEndian(page[Page.LsnFieldOffset..], lsn);
        page[Page.TypeFieldOffset] = (byte)PageType.CheckpointAnchor;
        BinaryPrimitives.WriteInt64LittleEndian(page[anchorGenerationOffset..], generation);
        BinaryPrimitives.WriteInt32LittleEndian(page[anchorSlotOffset..], slot);
        BinaryPrimitives.WriteInt32LittleEndian(page[anchorCountOffset..], entries.Length);
        BinaryPrimitives.WriteInt64LittleEndian(page[anchorNextOffset..], next);
        BinaryPrimitives.WriteInt32LittleEndian(page[anchorIndexOffset..], index);

        for (int i = 0; i < entries.Length; i++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(page[(anchorEntriesOffset + i * sizeof(long))..], entries[i]);
        }
    }

    /// <summary>
    /// Reads one page of a slot's anchor chain from raw page bytes. The page must verify its
    /// checksum, be an anchor page with the expected identity, belong to the expected slot,
    /// generation and chain position, and hold at least one entry.
    /// </summary>
    /// <returns>Null when the page is valid; otherwise why it is not.</returns>
    internal static string? TryReadAnchorPage(
        ReadOnlySpan<byte> page,
        long pageId,
        long generation,
        int slot,
        int index,
        out int count,
        out long next)
    {
        count = 0;
        next = 0;

        if (!PageChecksum.TryVerify(page, out uint stored, out uint computed))
        {
            return $"it failed checksum verification (stored 0x{stored:X8}, computed 0x{computed:X8})";
        }

        if ((PageType)page[Page.TypeFieldOffset] != PageType.CheckpointAnchor || BinaryPrimitives.ReadInt64LittleEndian(page) != pageId)
        {
            return $"it is not checkpoint anchor page {pageId} (type {(PageType)page[Page.TypeFieldOffset]})";
        }

        long pageGeneration = BinaryPrimitives.ReadInt64LittleEndian(page[anchorGenerationOffset..]);
        int pageSlot = BinaryPrimitives.ReadInt32LittleEndian(page[anchorSlotOffset..]);
        int pageIndex = BinaryPrimitives.ReadInt32LittleEndian(page[anchorIndexOffset..]);
        if (pageGeneration != generation || pageSlot != slot || pageIndex != index)
        {
            return $"it belongs to generation {pageGeneration}, slot {pageSlot}, position {pageIndex}, " +
                $"not generation {generation}, slot {slot}, position {index}";
        }

        count = BinaryPrimitives.ReadInt32LittleEndian(page[anchorCountOffset..]);
        next = BinaryPrimitives.ReadInt64LittleEndian(page[anchorNextOffset..]);
        if (count is <= 0 or > AnchorPageCapacity)
        {
            return $"it claims {count} entries; an anchor page holds 1 to {AnchorPageCapacity}";
        }

        return null;
    }

    /// <summary>
    /// Reads the entries of an anchor page that <see cref="TryReadAnchorPage"/> accepted.
    /// </summary>
    internal static void ReadAnchorEntries(ReadOnlySpan<byte> page, Span<long> destination)
    {
        for (int i = 0; i < destination.Length; i++)
        {
            destination[i] = BinaryPrimitives.ReadInt64LittleEndian(page[(anchorEntriesOffset + i * sizeof(long))..]);
        }
    }

    private static uint ComputeIdentityChecksum(ReadOnlySpan<byte> identity)
    {
        var block = identity[..StorageFileHeader.ByteSize];
        uint state = Crc32C.Begin();
        state = Crc32C.Append(state, block[..blockChecksumOffset]);
        state = Crc32C.AppendZeros(state, sizeof(uint));
        state = Crc32C.Append(state, block[(blockChecksumOffset + sizeof(uint))..]);
        return Crc32C.Finalize(state);
    }

    private static uint ComputeSlotChecksum(ReadOnlySpan<byte> slot)
    {
        uint state = Crc32C.Begin();
        state = Crc32C.Append(state, slot[..slotChecksumOffset]);
        state = Crc32C.AppendZeros(state, sizeof(uint));
        state = Crc32C.Append(state, slot[(slotChecksumOffset + sizeof(uint))..SlotSize]);
        return Crc32C.Finalize(state);
    }
}
