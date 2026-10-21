using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Assimalign.Cohesion.Database.Storage.Internal;

using Assimalign.Cohesion.Database.Storage.Units;

/// <summary>
/// The byte-run encoding of the journal's page records (storage format 3, #1253): a full page
/// image is the runs in which the page differs from an all-zero page, and a page delta is the
/// runs in which the page differs from the bracket's pre-image of it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Layout.</b> A run is <c>[u16 offset][u16 length][length bytes]</c>, little-endian. Runs are
/// strictly ascending, never overlap, are at least one byte long, and never cover the page's LSN
/// and checksum fields (bytes 8 to 19): recovery stamps the LSN of the record it applied, and the
/// buffer pool stamps the checksum on every write-back, so neither travels in a record.
/// </para>
/// <para>
/// <b>Finding runs.</b> Equal stretches are skipped with a vectorized compare
/// (<see cref="MemoryExtensions.CommonPrefixLength{T}(ReadOnlySpan{T}, ReadOnlySpan{T})"/>). From
/// the first differing byte, a run extends 32-byte block by block until an aligned block compares
/// equal, then loses its trailing equal bytes. Equal bytes inside a run travel with it: a run
/// header costs four bytes, so splitting a run at a gap shorter than a block would rarely pay, and
/// a B-tree insert, which shifts a directory of two-byte offsets whose high bytes often agree,
/// stays one run. RavenDB's Voron diffs pages the same way, in 32-byte blocks with a full-page
/// fallback (<c>DiffPages.ComputeDiff</c>, <c>src/Sparrow.Server/Utils/DiffPages.cs:23-105</c>),
/// and encodes a new page as a diff against zeros (<c>ComputeNew</c>, <c>:107-178</c>).
/// </para>
/// <para>
/// <b>Hole elision.</b> Encoding an image against zeros drops every all-zero block, which is the
/// free gap of both page formats with one: a slotted page's gap between its records and its slot
/// directory, and a B-tree node's gap between its entry directory and its entry data. The gap
/// starts zero: allocation clears the page, the storage clears a slotted page's body before it
/// reinitializes one (<c>SlottedPage.Initialize</c> itself only resets the header), and
/// <c>BTreeNode.Initialize</c> clears the node's body, which a split or a compaction rebuilds through,
/// and a B-tree entry removal clears the directory slot it vacates. A slotted page's
/// <c>Compact</c> leaves the old bytes between its new and its previous free-data end in the gap
/// until the page is reinitialized. So the encoding needs no page-layout contract. PostgreSQL elides the gap between
/// <c>pd_lower</c> and <c>pd_upper</c> of a standard page (<c>XLogRecordAssemble</c>,
/// <c>src/backend/access/transam/xloginsert.c:731-756</c>) and zero-fills it on restore
/// (<c>RestoreBlockImage</c>, <c>src/backend/access/transam/xlogreader.c:2213-2224</c>); a gap
/// that is not zero here costs bytes, never correctness.
/// </para>
/// </remarks>
internal static class PageImageCodec
{
    /// <summary>
    /// The size of a run header: a two-byte offset and a two-byte length.
    /// </summary>
    internal const int RunHeaderSize = sizeof(ushort) + sizeof(ushort);

    /// <summary>
    /// The block a run grows by: a run ends at the first aligned block that compares equal.
    /// </summary>
    internal const int BlockSize = 32;

    /// <summary>
    /// The first byte no run covers: the page LSN.
    /// </summary>
    internal const int MaskedStart = Page.LsnFieldOffset;

    /// <summary>
    /// The first byte after the bytes no run covers: the LSN and the checksum.
    /// </summary>
    internal const int MaskedEnd = Page.ChecksumFieldOffset + sizeof(uint);

    /// <summary>
    /// The largest encoding of a page: every byte outside the masked fields, in the three runs a
    /// page that differs everywhere produces (bytes 0 to 7, 20 to 31 and 32 to the end). Splitting a
    /// run takes at least one equal block, which saves more than the header it adds, so no other
    /// page encodes longer.
    /// </summary>
    internal const int MaximumRunsLength = Page.Size - (MaskedEnd - MaskedStart) + (3 * RunHeaderSize);

    /// <summary>
    /// The size of the base LSN that leads a page delta and a committed page image.
    /// </summary>
    internal const int BaseLsnSize = sizeof(long);

    /// <summary>
    /// The largest payload of a page record: a base LSN and the longest encoding.
    /// </summary>
    internal const int MaximumPayloadLength = BaseLsnSize + MaximumRunsLength;

    /// <summary>
    /// The delta length past which a commit journals a committed full image of the page instead
    /// (about half a page), unless the image is longer than the delta by more than
    /// <see cref="CommittedImageSlack"/>: a rewritten Blob page changes most of its bytes, and a
    /// page a delete cleared encodes as little more than its header.
    /// </summary>
    internal const int CommittedImageThreshold = Page.Size / 2;

    /// <summary>
    /// How much longer than the delta a committed image may be and still replace it: the header
    /// fields an image carries and a delta of a rewritten page does not (the page id, type and
    /// owner tag), a few short runs. A committed image needs no base content to apply.
    /// </summary>
    internal const int CommittedImageSlack = 64;

    // The reference an image is encoded against.
    private static readonly byte[] ZeroPage = new byte[Page.Size];

    /// <summary>
    /// Encodes the full image of <paramref name="page"/>: the runs in which it differs from an
    /// all-zero page.
    /// </summary>
    /// <param name="page">The page, <see cref="Page.Size"/> bytes.</param>
    /// <param name="destination">At least <see cref="MaximumRunsLength"/> bytes.</param>
    /// <returns>The number of bytes written.</returns>
    internal static int EncodeImage(ReadOnlySpan<byte> page, Span<byte> destination)
        => EncodeRuns(ZeroPage, page, destination);

    /// <summary>
    /// Encodes the runs in which <paramref name="current"/> differs from
    /// <paramref name="reference"/>, outside the masked LSN and checksum fields.
    /// </summary>
    /// <param name="reference">The page the runs apply to, <see cref="Page.Size"/> bytes.</param>
    /// <param name="current">The page the runs produce, <see cref="Page.Size"/> bytes.</param>
    /// <param name="destination">At least <see cref="MaximumRunsLength"/> bytes.</param>
    /// <returns>The number of bytes written; zero when the pages agree outside the masked fields.</returns>
    internal static int EncodeRuns(ReadOnlySpan<byte> reference, ReadOnlySpan<byte> current, Span<byte> destination)
    {
        if (reference.Length != Page.Size || current.Length != Page.Size)
        {
            throw new ArgumentException($"A page is {Page.Size} bytes.");
        }

        if (destination.Length < MaximumRunsLength)
        {
            throw new ArgumentException($"The destination must hold {MaximumRunsLength} bytes.", nameof(destination));
        }

        int written = EncodeSegment(reference, current, 0, MaskedStart, destination, 0);
        written = EncodeSegment(reference, current, MaskedEnd, BlockSize, destination, written);

        int position = BlockSize;
        while (position < Page.Size)
        {
            position += reference[position..].CommonPrefixLength(current[position..]);
            if (position >= Page.Size)
            {
                break;
            }

            int start = position;
            int blockEnd = ((start / BlockSize) + 1) * BlockSize;
            while (blockEnd < Page.Size && !BlockEquals(reference, current, blockEnd))
            {
                blockEnd += BlockSize;
            }

            // The last block of the run differs (it holds the start, or compared unequal), so the
            // trim stops inside it.
            int end = blockEnd;
            while (reference[end - 1] == current[end - 1])
            {
                end--;
            }

            written = WriteRun(destination, written, start, current[start..end]);
            position = blockEnd;
        }

        return written;
    }

    /// <summary>
    /// Restores a full page image into <paramref name="page"/>: every byte outside the masked LSN
    /// and checksum fields is cleared, then the runs are written. The masked fields keep their
    /// bytes; the caller sets the LSN.
    /// </summary>
    /// <param name="runs">The image's runs.</param>
    /// <param name="page">The page to restore, <see cref="Page.Size"/> bytes.</param>
    /// <returns>Null when the runs applied; otherwise why they are malformed, with the page unspecified.</returns>
    internal static string? TryApplyImage(ReadOnlySpan<byte> runs, Span<byte> page)
    {
        page[..MaskedStart].Clear();
        page[MaskedEnd..].Clear();
        return TryApplyRuns(runs, page);
    }

    /// <summary>
    /// Writes byte runs into <paramref name="page"/>, after checking each one.
    /// </summary>
    /// <param name="runs">The runs, as <see cref="EncodeRuns"/> wrote them.</param>
    /// <param name="page">The page to change, <see cref="Page.Size"/> bytes.</param>
    /// <returns>Null when the runs applied; otherwise why they are malformed, with the page partly changed.</returns>
    internal static string? TryApplyRuns(ReadOnlySpan<byte> runs, Span<byte> page)
    {
        int offset = 0;
        int previousEnd = 0;
        while (offset < runs.Length)
        {
            if (runs.Length - offset < RunHeaderSize)
            {
                return $"a run header at payload offset {offset} is cut short";
            }

            int start = BinaryPrimitives.ReadUInt16LittleEndian(runs[offset..]);
            int length = BinaryPrimitives.ReadUInt16LittleEndian(runs[(offset + sizeof(ushort))..]);
            offset += RunHeaderSize;

            if (length == 0)
            {
                return $"the run at page offset {start} is empty";
            }

            if (start < previousEnd)
            {
                return $"the run at page offset {start} overlaps or precedes the run before it, which ends at {previousEnd}";
            }

            if (start + length > Page.Size)
            {
                return $"the run at page offset {start} of {length} bytes passes the end of the page";
            }

            if (start < MaskedEnd && start + length > MaskedStart)
            {
                return $"the run at page offset {start} of {length} bytes covers the page LSN or checksum";
            }

            if (runs.Length - offset < length)
            {
                return $"the run at page offset {start} declares {length} bytes, but the payload holds {runs.Length - offset}";
            }

            runs.Slice(offset, length).CopyTo(page.Slice(start, length));
            offset += length;
            previousEnd = start + length;
        }

        return null;
    }

    /// <summary>
    /// Finds the first difference in <paramref name="page"/> from <paramref name="reference"/>
    /// outside the masked fields (diagnostics).
    /// </summary>
    /// <returns>The offset of the first differing byte, or -1 when the pages agree.</returns>
    internal static int FirstDifference(ReadOnlySpan<byte> reference, ReadOnlySpan<byte> page)
    {
        int head = reference[..MaskedStart].CommonPrefixLength(page[..MaskedStart]);
        if (head < MaskedStart)
        {
            return head;
        }

        int tail = reference[MaskedEnd..].CommonPrefixLength(page[MaskedEnd..]);
        return MaskedEnd + tail < Page.Size ? MaskedEnd + tail : -1;
    }

    /// <summary>
    /// Encodes the differing bytes of a short segment as at most one run.
    /// </summary>
    private static int EncodeSegment(ReadOnlySpan<byte> reference, ReadOnlySpan<byte> current, int start, int end, Span<byte> destination, int written)
    {
        int first = start;
        while (first < end && reference[first] == current[first])
        {
            first++;
        }

        if (first == end)
        {
            return written;
        }

        int last = end - 1;
        while (reference[last] == current[last])
        {
            last--;
        }

        return WriteRun(destination, written, first, current[first..(last + 1)]);
    }

    private static int WriteRun(Span<byte> destination, int written, int offset, ReadOnlySpan<byte> bytes)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(destination[written..], (ushort)offset);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[(written + sizeof(ushort))..], (ushort)bytes.Length);
        bytes.CopyTo(destination[(written + RunHeaderSize)..]);
        return written + RunHeaderSize + bytes.Length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool BlockEquals(ReadOnlySpan<byte> reference, ReadOnlySpan<byte> current, int offset)
    {
        ref byte left = ref Unsafe.Add(ref MemoryMarshal.GetReference(reference), offset);
        ref byte right = ref Unsafe.Add(ref MemoryMarshal.GetReference(current), offset);
        ulong difference =
            (Unsafe.ReadUnaligned<ulong>(ref left) ^ Unsafe.ReadUnaligned<ulong>(ref right)) |
            (Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref left, 8)) ^ Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref right, 8))) |
            (Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref left, 16)) ^ Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref right, 16))) |
            (Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref left, 24)) ^ Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref right, 24)));
        return difference == 0;
    }
}
