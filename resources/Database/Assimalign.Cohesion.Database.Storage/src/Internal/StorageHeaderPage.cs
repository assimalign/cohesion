using System;
using System.Buffers.Binary;
using System.Text;

namespace Assimalign.Cohesion.Database.Storage.Internal;

using Assimalign.Cohesion.Database.Storage.Units;

/// <summary>
/// The state one header slot of page 0 records (storage format 2).
/// </summary>
/// <param name="Generation">The header generation; the newest valid slot wins at open.</param>
/// <param name="LsnFloor">The journal's last LSN when the generation was written: the next LSN after a reopen exceeds it.</param>
/// <param name="SequenceFloor">The highest transaction sequence assigned when the generation was written.</param>
/// <param name="TotalPageCount">The data file's page count (informational; open recomputes it from the file length).</param>
/// <param name="FreePageCount">The free page count (informational; open rebuilds the free-space map from page headers).</param>
/// <param name="ModifiedAtUtcTicks">When the generation was written.</param>
/// <param name="AnchorCount">The number of logical transaction sequences in the checkpoint anchor.</param>
/// <param name="AnchorInlineCount">How many of them the slot holds itself; the rest are on the anchor chain.</param>
/// <param name="AnchorChainHead">The first anchor page, or zero when the slot holds the whole anchor.</param>
/// <param name="AnchorChainPageCount">The number of anchor pages on the chain.</param>
internal sealed record StorageHeaderSlot(
    long Generation,
    long LsnFloor,
    long SequenceFloor,
    long TotalPageCount,
    long FreePageCount,
    long ModifiedAtUtcTicks,
    int AnchorCount,
    int AnchorInlineCount,
    long AnchorChainHead,
    int AnchorChainPageCount);

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
/// anchor count (60), anchor chain head (64), anchor chain page count (72), then the inline
/// anchor sequences as little-endian 64-bit values from offset 80.
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

    private const int IdentityChecksumOffset = IdentityOffset + 168;
    private const int IdentityModelOffset = IdentityOffset + 12;
    private const int IdentityPageSizeOffset = IdentityOffset + 8;
    private const int IdentityStorageIdOffset = IdentityOffset + 16;
    private const int IdentityCreatedOffset = IdentityOffset + 32;
    private const int IdentityNameOffset = IdentityOffset + 40;
    private const int NameCapacity = 128;

    /// <summary>The size of one header slot.</summary>
    internal const int SlotSize = 3584;

    /// <summary>The offset of header slot 0 in page 0.</summary>
    internal const int Slot0Offset = 512;

    /// <summary>The offset of header slot 1 in page 0.</summary>
    internal const int Slot1Offset = 4608;

    private const int SlotMagicValue = 0x544F4C53; // "SLOT"
    private const int SlotMagicOffset = 0;
    private const int SlotChecksumOffset = 4;
    private const int SlotGenerationOffset = 8;
    private const int SlotLsnFloorOffset = 16;
    private const int SlotSequenceFloorOffset = 24;
    private const int SlotTotalPagesOffset = 32;
    private const int SlotFreePagesOffset = 40;
    private const int SlotModifiedOffset = 48;
    private const int SlotAnchorCountOffset = 56;
    private const int SlotAnchorInlineOffset = 60;
    private const int SlotAnchorChainHeadOffset = 64;
    private const int SlotAnchorChainPagesOffset = 72;
    private const int SlotHeaderSize = 80;

    /// <summary>The most anchor sequences a slot holds itself.</summary>
    internal const int InlineAnchorCapacity = (SlotSize - SlotHeaderSize) / sizeof(long);

    private const int AnchorGenerationOffset = Page.HeaderSize;
    private const int AnchorSlotOffset = Page.HeaderSize + 8;
    private const int AnchorCountOffset = Page.HeaderSize + 12;
    private const int AnchorNextOffset = Page.HeaderSize + 16;
    private const int AnchorIndexOffset = Page.HeaderSize + 24;
    private const int AnchorEntriesOffset = Page.HeaderSize + 32;

    /// <summary>The most anchor sequences one anchor page holds.</summary>
    internal const int AnchorPageCapacity = (Page.Size - AnchorEntriesOffset) / sizeof(long);

    /// <summary>Gets the offset of a header slot in page 0.</summary>
    internal static int SlotOffset(int slot) => slot == 0 ? Slot0Offset : Slot1Offset;

    /// <summary>
    /// Writes a new page 0: the page header (never checksummed) and the identity block.
    /// Both slots are left zero, which never verifies.
    /// </summary>
    internal static void Initialize(Span<byte> page0, StorageId id, Name name, StorageModel model, long createdAtUtcTicks)
    {
        page0[..Page.Size].Clear();
        BinaryPrimitives.WriteInt64LittleEndian(page0, 0L); // page id
        page0[Page.TypeFieldOffset] = (byte)PageType.FileHeader;

        BinaryPrimitives.WriteInt32LittleEndian(page0[MagicOffset..], StorageFileHeader.ExpectedMagic);
        BinaryPrimitives.WriteInt32LittleEndian(page0[FormatVersionOffset..], StorageFileHeader.CurrentFormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(page0[IdentityPageSizeOffset..], Page.Size);
        BinaryPrimitives.WriteInt32LittleEndian(page0[IdentityModelOffset..], (int)model);
        ((Guid)id).TryWriteBytes(page0.Slice(IdentityStorageIdOffset, 16));
        BinaryPrimitives.WriteInt64LittleEndian(page0[IdentityCreatedOffset..], createdAtUtcTicks);

        string? text = (string?)name;
        if (!string.IsNullOrEmpty(text))
        {
            byte[] encoded = Encoding.UTF8.GetBytes(text);
            encoded.AsSpan(0, Math.Min(encoded.Length, NameCapacity)).CopyTo(page0[IdentityNameOffset..]);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(page0[IdentityChecksumOffset..], ComputeIdentityChecksum(page0));
    }

    /// <summary>
    /// Reads the magic number and format version from raw page-0 bytes, at the offsets every
    /// storage format shares, before anything is verified.
    /// </summary>
    internal static (int Magic, int FormatVersion) ReadFormat(ReadOnlySpan<byte> page0)
        => (BinaryPrimitives.ReadInt32LittleEndian(page0[MagicOffset..]),
            BinaryPrimitives.ReadInt32LittleEndian(page0[FormatVersionOffset..]));

    /// <summary>
    /// Verifies the identity block's checksum.
    /// </summary>
    internal static bool VerifyIdentity(ReadOnlySpan<byte> page0)
        => BinaryPrimitives.ReadUInt32LittleEndian(page0[IdentityChecksumOffset..]) == ComputeIdentityChecksum(page0);

    /// <summary>
    /// Reads the storage identifier and name from a verified identity block.
    /// </summary>
    internal static (StorageId Id, Name Name) ReadIdentity(ReadOnlySpan<byte> page0)
    {
        var id = (StorageId)new Guid(page0.Slice(IdentityStorageIdOffset, 16));
        var nameBytes = page0.Slice(IdentityNameOffset, NameCapacity);
        int length = nameBytes.IndexOf((byte)0);
        if (length < 0)
        {
            length = NameCapacity;
        }

        var name = length > 0 ? (Name)Encoding.UTF8.GetString(nameBytes[..length]) : (Name)"";
        return (id, name);
    }

    /// <summary>
    /// Composes a header slot: its fields, its inline anchor sequences, zeros after them,
    /// and its checksum.
    /// </summary>
    internal static void WriteSlot(Span<byte> slot, StorageHeaderSlot state, ReadOnlySpan<long> inlineAnchor)
    {
        if (inlineAnchor.Length != state.AnchorInlineCount || inlineAnchor.Length > InlineAnchorCapacity)
        {
            throw new ArgumentException("The inline anchor does not match the slot state.", nameof(inlineAnchor));
        }

        slot = slot[..SlotSize];
        slot.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(slot[SlotMagicOffset..], SlotMagicValue);
        BinaryPrimitives.WriteInt64LittleEndian(slot[SlotGenerationOffset..], state.Generation);
        BinaryPrimitives.WriteInt64LittleEndian(slot[SlotLsnFloorOffset..], state.LsnFloor);
        BinaryPrimitives.WriteInt64LittleEndian(slot[SlotSequenceFloorOffset..], state.SequenceFloor);
        BinaryPrimitives.WriteInt64LittleEndian(slot[SlotTotalPagesOffset..], state.TotalPageCount);
        BinaryPrimitives.WriteInt64LittleEndian(slot[SlotFreePagesOffset..], state.FreePageCount);
        BinaryPrimitives.WriteInt64LittleEndian(slot[SlotModifiedOffset..], state.ModifiedAtUtcTicks);
        BinaryPrimitives.WriteInt32LittleEndian(slot[SlotAnchorCountOffset..], state.AnchorCount);
        BinaryPrimitives.WriteInt32LittleEndian(slot[SlotAnchorInlineOffset..], state.AnchorInlineCount);
        BinaryPrimitives.WriteInt64LittleEndian(slot[SlotAnchorChainHeadOffset..], state.AnchorChainHead);
        BinaryPrimitives.WriteInt32LittleEndian(slot[SlotAnchorChainPagesOffset..], state.AnchorChainPageCount);

        for (int i = 0; i < inlineAnchor.Length; i++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(slot[(SlotHeaderSize + i * sizeof(long))..], inlineAnchor[i]);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(slot[SlotChecksumOffset..], ComputeSlotChecksum(slot));
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

        if (BinaryPrimitives.ReadInt32LittleEndian(slot[SlotMagicOffset..]) != SlotMagicValue
            || BinaryPrimitives.ReadUInt32LittleEndian(slot[SlotChecksumOffset..]) != ComputeSlotChecksum(slot))
        {
            return false;
        }

        var candidate = new StorageHeaderSlot(
            Generation: BinaryPrimitives.ReadInt64LittleEndian(slot[SlotGenerationOffset..]),
            LsnFloor: BinaryPrimitives.ReadInt64LittleEndian(slot[SlotLsnFloorOffset..]),
            SequenceFloor: BinaryPrimitives.ReadInt64LittleEndian(slot[SlotSequenceFloorOffset..]),
            TotalPageCount: BinaryPrimitives.ReadInt64LittleEndian(slot[SlotTotalPagesOffset..]),
            FreePageCount: BinaryPrimitives.ReadInt64LittleEndian(slot[SlotFreePagesOffset..]),
            ModifiedAtUtcTicks: BinaryPrimitives.ReadInt64LittleEndian(slot[SlotModifiedOffset..]),
            AnchorCount: BinaryPrimitives.ReadInt32LittleEndian(slot[SlotAnchorCountOffset..]),
            AnchorInlineCount: BinaryPrimitives.ReadInt32LittleEndian(slot[SlotAnchorInlineOffset..]),
            AnchorChainHead: BinaryPrimitives.ReadInt64LittleEndian(slot[SlotAnchorChainHeadOffset..]),
            AnchorChainPageCount: BinaryPrimitives.ReadInt32LittleEndian(slot[SlotAnchorChainPagesOffset..]));

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
            destination[i] = BinaryPrimitives.ReadInt64LittleEndian(slot[(SlotHeaderSize + i * sizeof(long))..]);
        }
    }

    /// <summary>
    /// Composes an anchor page in a page buffer: the page header (the pool stamps the
    /// checksum when it writes the page back) and the chain fields and entries.
    /// </summary>
    internal static void WriteAnchorPage(Span<byte> page, long pageId, long generation, int slot, int index, ReadOnlySpan<long> entries, long next)
    {
        if (entries.Length is 0 or > AnchorPageCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(entries), entries.Length, "An anchor page holds between 1 and its capacity of entries.");
        }

        page = page[..Page.Size];
        page.Clear();
        BinaryPrimitives.WriteInt64LittleEndian(page, pageId);
        page[Page.TypeFieldOffset] = (byte)PageType.CheckpointAnchor;
        BinaryPrimitives.WriteInt64LittleEndian(page[AnchorGenerationOffset..], generation);
        BinaryPrimitives.WriteInt32LittleEndian(page[AnchorSlotOffset..], slot);
        BinaryPrimitives.WriteInt32LittleEndian(page[AnchorCountOffset..], entries.Length);
        BinaryPrimitives.WriteInt64LittleEndian(page[AnchorNextOffset..], next);
        BinaryPrimitives.WriteInt32LittleEndian(page[AnchorIndexOffset..], index);

        for (int i = 0; i < entries.Length; i++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(page[(AnchorEntriesOffset + i * sizeof(long))..], entries[i]);
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

        long pageGeneration = BinaryPrimitives.ReadInt64LittleEndian(page[AnchorGenerationOffset..]);
        int pageSlot = BinaryPrimitives.ReadInt32LittleEndian(page[AnchorSlotOffset..]);
        int pageIndex = BinaryPrimitives.ReadInt32LittleEndian(page[AnchorIndexOffset..]);
        if (pageGeneration != generation || pageSlot != slot || pageIndex != index)
        {
            return $"it belongs to generation {pageGeneration}, slot {pageSlot}, position {pageIndex}, " +
                $"not generation {generation}, slot {slot}, position {index}";
        }

        count = BinaryPrimitives.ReadInt32LittleEndian(page[AnchorCountOffset..]);
        next = BinaryPrimitives.ReadInt64LittleEndian(page[AnchorNextOffset..]);
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
            destination[i] = BinaryPrimitives.ReadInt64LittleEndian(page[(AnchorEntriesOffset + i * sizeof(long))..]);
        }
    }

    private static uint ComputeIdentityChecksum(ReadOnlySpan<byte> page0)
    {
        var block = page0.Slice(IdentityOffset, StorageFileHeader.ByteSize);
        int checksumAt = IdentityChecksumOffset - IdentityOffset;
        uint state = Crc32C.Begin();
        state = Crc32C.Append(state, block[..checksumAt]);
        state = Crc32C.AppendZeros(state, sizeof(uint));
        state = Crc32C.Append(state, block[(checksumAt + sizeof(uint))..]);
        return Crc32C.Finalize(state);
    }

    private static uint ComputeSlotChecksum(ReadOnlySpan<byte> slot)
    {
        uint state = Crc32C.Begin();
        state = Crc32C.Append(state, slot[..SlotChecksumOffset]);
        state = Crc32C.AppendZeros(state, sizeof(uint));
        state = Crc32C.Append(state, slot[(SlotChecksumOffset + sizeof(uint))..SlotSize]);
        return Crc32C.Finalize(state);
    }
}
