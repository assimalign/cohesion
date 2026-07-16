using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Assimalign.Cohesion.Database.Storage.Units;

/// <summary>
/// Represents a fixed-size 8KB page buffer with a 96-byte header.
/// This is the fundamental unit of storage I/O shared by all database models.
/// </summary>
/// <remarks>
/// The page is an unsafe struct that provides direct pointer access to the underlying
/// buffer. The caller is responsible for ensuring the buffer remains valid for the
/// lifetime of this struct.
/// </remarks>
public readonly unsafe struct Page
{
    /// <summary>
    /// Initializes a new <see cref="Page"/> over an existing buffer pointer.
    /// </summary>
    /// <param name="pointer">A pointer to the start of an 8KB buffer.</param>
    public Page(byte* pointer)
    {
        Pointer = pointer;
    }

    /// <summary>
    /// The standard page size in bytes (8KB).
    /// </summary>
    public const int Size = 8192;

    /// <summary>
    /// The size of the page header in bytes.
    /// </summary>
    public const int HeaderSize = 96;

    /// <summary>
    /// The byte offset of the page checksum field within the header. The checksum
    /// covers the entire page with these four bytes treated as zero.
    /// </summary>
    public const int ChecksumFieldOffset = 16;

    /// <summary>
    /// A pointer to the start of the page buffer.
    /// </summary>
    public readonly byte* Pointer;

    #region Page Header

    /// <summary>
    /// Gets or sets the unique identifier for this page within a storage file.
    /// </summary>
    public long Id
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ((Header*)Pointer)->PageId;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => ((Header*)Pointer)->PageId = value;
    }

    /// <summary>
    /// Gets or sets the log sequence number (LSN) of the most recent journal record
    /// that modified this page.
    /// </summary>
    /// <remarks>
    /// The LSN enforces the write-ahead rule: a page may only be written to the data
    /// stream once every journal record up to and including this LSN is durable. During
    /// recovery, a journal record is applied to the page only when the record's LSN is
    /// newer than the LSN persisted here, making replay idempotent.
    /// </remarks>
    public long Lsn
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ((Header*)Pointer)->Lsn;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => ((Header*)Pointer)->Lsn = value;
    }

    /// <summary>
    /// Gets or sets the CRC-32 integrity checksum for this page.
    /// </summary>
    /// <remarks>
    /// The checksum is computed over the full page with the checksum field itself
    /// treated as zero. It is stamped by the buffer pool on every write-back and
    /// verified on every load from the storage stream. A stored value of zero means
    /// the page has never been stamped (a freshly allocated page) and is not verified.
    /// </remarks>
    public uint Checksum
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ((Header*)Pointer)->Checksum;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => ((Header*)Pointer)->Checksum = value;
    }

    /// <summary>
    /// Gets or sets the type discriminator for this page.
    /// </summary>
    public PageType Type
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ((Header*)Pointer)->Type;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => ((Header*)Pointer)->Type = value;
    }

    /// <summary>
    /// Gets or sets the page state flags.
    /// </summary>
    public PageFlags Flags
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ((Header*)Pointer)->Flags;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => ((Header*)Pointer)->Flags = value;
    }

    /// <summary>
    /// Gets a value indicating whether this page is an overflow page.
    /// </summary>
    public bool IsOverflow
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (((Header*)Pointer)->Flags & PageFlags.Overflow) == PageFlags.Overflow;
    }

    /// <summary>
    /// Gets or sets the overflow size in bytes beyond the standard page size.
    /// Only meaningful when <see cref="IsOverflow"/> is <c>true</c>.
    /// </summary>
    public int OverflowSize
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ((Header*)Pointer)->OverflowSize;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => ((Header*)Pointer)->OverflowSize = value;
    }

    /// <summary>
    /// Gets or sets the identity of the object (table, collection, container) whose
    /// records this page holds — the per-object record-chain tag. Zero means the
    /// page belongs to the shared, untagged record space (the pre-chain layout, and
    /// the layout metadata-style consumers keep using).
    /// </summary>
    /// <remarks>
    /// The owner tag is model-agnostic: the storage layer never interprets it beyond
    /// grouping data pages into per-owner chains for scoped iteration and release.
    /// Files written before the tag existed read zero here (the field occupies
    /// previously reserved header bytes), which is exactly the shared-space meaning.
    /// </remarks>
    public ulong OwnerId
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ((Header*)Pointer)->OwnerId;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => ((Header*)Pointer)->OwnerId = value;
    }

    #endregion

    /// <summary>
    /// Returns the page buffer as a <see cref="Span{T}"/> of bytes.
    /// For overflow pages, the span includes the overflow area.
    /// </summary>
    /// <returns>A span over the full page buffer.</returns>
    public Span<byte> AsSpan()
    {
        return new Span<byte>(Pointer, IsOverflow ? OverflowSize + HeaderSize : Size);
    }

    /// <summary>
    /// Returns the page body (data area after the header) as a <see cref="Span{T}"/> of bytes.
    /// </summary>
    /// <returns>A span over the page body.</returns>
    public Span<byte> AsBodySpan()
    {
        int bodySize = (IsOverflow ? OverflowSize + HeaderSize : Size) - HeaderSize;
        return new Span<byte>(Pointer + HeaderSize, bodySize);
    }

    /// <summary>
    /// The binary layout of the page header. Occupies the first 96 bytes of every page.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 96, Pack = 1)]
    internal unsafe struct Header
    {
        /// <summary>
        /// Unique page identifier (8 bytes).
        /// </summary>
        [FieldOffset(0)]
        public long PageId;

        /// <summary>
        /// Log sequence number of the most recent journal record that modified
        /// this page (8 bytes). Drives the write-ahead rule and idempotent replay.
        /// </summary>
        [FieldOffset(8)]
        public long Lsn;

        /// <summary>
        /// CRC-32 integrity checksum over the full page with this field zeroed (4 bytes).
        /// Zero means the page has never been stamped.
        /// </summary>
        [FieldOffset(16)]
        public uint Checksum;

        /// <summary>
        /// Page state flags (1 byte).
        /// </summary>
        [FieldOffset(20)]
        public PageFlags Flags;

        /// <summary>
        /// Page type discriminator (1 byte).
        /// </summary>
        [FieldOffset(21)]
        public PageType Type;

        /// <summary>
        /// Number of record slots in the page (2 bytes).
        /// Used by slotted page operations.
        /// </summary>
        [FieldOffset(22)]
        public ushort SlotCount;

        /// <summary>
        /// Byte offset (from page start) where record data ends and free space begins (2 bytes).
        /// Used by slotted page operations.
        /// </summary>
        [FieldOffset(24)]
        public ushort FreeDataEnd;

        /// <summary>
        /// Overflow size in bytes beyond the standard page (4 bytes).
        /// </summary>
        [FieldOffset(28)]
        public int OverflowSize;

        /// <summary>
        /// Cryptographic nonce reserved for authenticated (encrypted-at-rest) pages (16 bytes).
        /// </summary>
        [FieldOffset(32)]
        public fixed byte Nonce[16];

        /// <summary>
        /// Message authentication code reserved for authenticated (encrypted-at-rest) pages (16 bytes).
        /// </summary>
        [FieldOffset(48)]
        public fixed byte Mac[16];

        /// <summary>
        /// Identity of the object whose records this page holds (8 bytes); zero for
        /// the shared, untagged record space. Drives per-object record chains.
        /// </summary>
        [FieldOffset(64)]
        public ulong OwnerId;

        /// <summary>
        /// Reserved for future use (24 bytes).
        /// </summary>
        [FieldOffset(72)]
        public fixed byte Reserved[24];
    }
}
