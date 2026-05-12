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
        /// Overflow size in bytes beyond the standard page (4 bytes).
        /// </summary>
        [FieldOffset(8)]
        public int OverflowSize;

        /// <summary>
        /// Page state flags (1 byte).
        /// </summary>
        [FieldOffset(12)]
        public PageFlags Flags;

        /// <summary>
        /// Page type discriminator (1 byte).
        /// </summary>
        [FieldOffset(13)]
        public PageType Type;

        /// <summary>
        /// Number of record slots in the page (2 bytes).
        /// Used by slotted page operations.
        /// </summary>
        [FieldOffset(14)]
        public ushort SlotCount;

        /// <summary>
        /// Byte offset (from page start) where record data ends and free space begins (2 bytes).
        /// Used by slotted page operations.
        /// </summary>
        [FieldOffset(16)]
        public ushort FreeDataEnd;

        /// <summary>
        /// Reserved for future use (12 bytes).
        /// </summary>
        [FieldOffset(18)]
        public fixed byte Reserved[14];

        /// <summary>
        /// Non-cryptographic integrity checksum (2 bytes).
        /// Overlaps bytes 4-5 of PageId as a union—used only when crypto is disabled.
        /// </summary>
        [FieldOffset(4)]
        public short Checksum;

        /// <summary>
        /// Cryptographic nonce for authenticated pages (16 bytes).
        /// </summary>
        [FieldOffset(32)]
        public fixed byte Nonce[16];

        /// <summary>
        /// Message authentication code for authenticated pages (16 bytes).
        /// </summary>
        [FieldOffset(48)]
        public fixed byte Mac[16];
    }
}
