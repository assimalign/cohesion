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
    /// <exception cref="ArgumentNullException"><paramref name="pointer"/> is null.</exception>
    public Page(byte* pointer)
        : this(pointer, Size)
    {
    }

    /// <summary>Initializes a page over caller-owned memory with an explicit buffer length.</summary>
    /// <param name="pointer">A pointer kept valid by the caller for this page's lifetime.</param>
    /// <param name="bufferLength">The accessible byte count, at least <see cref="Size"/>; extra bytes are not part of the page.</param>
    /// <exception cref="ArgumentNullException"><paramref name="pointer"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bufferLength"/> is less than <see cref="Size"/>.</exception>
    public Page(byte* pointer, int bufferLength)
    {
        if (pointer == null)
        {
            throw new ArgumentNullException(nameof(pointer));
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(bufferLength, Size);
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
    /// The byte offset of the page LSN within the header.
    /// </summary>
    internal const int LsnFieldOffset = 8;

    /// <summary>
    /// The byte offset of the page type within the header.
    /// </summary>
    internal const int TypeFieldOffset = 21;

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
    /// stream once every journal record up to and including this LSN is durable. It also
    /// chains the page's journal records (storage format 3): a page delta names the LSN it
    /// applies on top of, recovery applies it only to a page rebuilt at exactly that LSN, and
    /// a page at or below the last checkpoint's LSN journals its full image before its next
    /// change. Allocation zeroes it, so a reallocated page is always imaged again.
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
    /// verified on every load from the storage stream. A stored value of zero is accepted only when
    /// the entire page is zero-filled. Every nonzero pooled page requires a checksum.
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
    /// Gets or sets the body length for the reserved overflow layout, within this fixed-size page.
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
    /// For the reserved overflow layout, the span contains the header and its declared body.
    /// </summary>
    /// <returns>A span over the full page buffer.</returns>
    /// <exception cref="StorageCorruptionException">The overflow body length does not fit the page.</exception>
    /// <exception cref="InvalidOperationException">This is an uninitialized page.</exception>
    public Span<byte> AsSpan()
    {
        return new Span<byte>(Pointer, HeaderSize + GetValidatedBodySize());
    }

    /// <summary>
    /// Returns the page body (data area after the header) as a <see cref="Span{T}"/> of bytes.
    /// </summary>
    /// <returns>A span over the page body.</returns>
    /// <exception cref="StorageCorruptionException">The overflow body length does not fit the page.</exception>
    /// <exception cref="InvalidOperationException">This is an uninitialized page.</exception>
    public Span<byte> AsBodySpan()
    {
        int bodySize = GetValidatedBodySize();
        return new Span<byte>(Pointer + HeaderSize, bodySize);
    }

    // Pool loads use the physical page id for the diagnostic; callers use the header id.
    // Snapshot and validate the size once: a second header read after checking could race
    // a caller's header change and manufacture an unbounded span (#1162).
    internal int GetValidatedBodySize(PageId? pageId = null)
    {
        if (Pointer == null)
        {
            throw new InvalidOperationException(ErrorMessages.PageBufferUninitialized);
        }

        var header = (Header*)Pointer;
        if ((header->Flags & PageFlags.Overflow) == 0)
        {
            return Size - HeaderSize;
        }

        int bodySize = header->OverflowSize;
        if ((uint)bodySize > Size - HeaderSize)
        {
            var id = pageId ?? (PageId)header->PageId;
            throw new StorageCorruptionException(id, string.Format(System.Globalization.CultureInfo.CurrentCulture,
                ErrorMessages.PageOverflowOutOfBounds, (long)id, bodySize, Size));
        }
        return bodySize;
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
        /// Body length for the reserved overflow layout, bounded by the fixed page (4 bytes).
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
