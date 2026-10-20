using System;
using System.Buffers.Binary;

namespace Assimalign.Cohesion.Database.Storage.Internal;

using Assimalign.Cohesion.Database.Storage.Units;

/// <summary>
/// Computes, stamps, and verifies the per-page CRC-32C integrity checksum
/// (<see cref="Crc32C"/>, storage format 2). The checksum covers the full page with the
/// four checksum bytes treated as zero, so a page can be verified in place without
/// copying.
/// </summary>
internal static class PageChecksum
{
    /// <summary>
    /// Computes the checksum of a full page buffer, treating the checksum field as zero.
    /// </summary>
    internal static uint Compute(ReadOnlySpan<byte> page)
    {
        uint state = Crc32C.Begin();
        state = Crc32C.Append(state, page[..Page.ChecksumFieldOffset]);
        state = Crc32C.AppendZeros(state, sizeof(uint));
        state = Crc32C.Append(state, page[(Page.ChecksumFieldOffset + sizeof(uint))..]);
        return Crc32C.Finalize(state);
    }

    /// <summary>
    /// Computes and writes the checksum into the page buffer's checksum field.
    /// </summary>
    internal static void Stamp(Span<byte> page)
    {
        uint checksum = Compute(page);
        BinaryPrimitives.WriteUInt32LittleEndian(page.Slice(Page.ChecksumFieldOffset, sizeof(uint)), checksum);
    }

    /// <summary>
    /// Verifies the checksum stored in the page buffer against its content.
    /// </summary>
    /// <param name="page">The full page buffer.</param>
    /// <param name="pageId">The page identifier, for the exception message.</param>
    /// <exception cref="StorageCorruptionException">The stored checksum does not match the page content.</exception>
    /// <remarks>
    /// A stored checksum of zero means the page was never stamped (a freshly allocated
    /// page written before checksumming existed for it) and is not verified.
    /// </remarks>
    internal static void Verify(ReadOnlySpan<byte> page, PageId pageId)
    {
        if (!TryVerify(page, out uint stored, out uint computed))
        {
            throw new StorageCorruptionException(
                pageId,
                $"Page {(long)pageId} failed checksum verification (stored 0x{stored:X8}, computed 0x{computed:X8}).");
        }
    }

    /// <summary>
    /// Verifies the checksum stored in the page buffer against its content without
    /// throwing: true when the checksum matches or was never stamped.
    /// </summary>
    internal static bool TryVerify(ReadOnlySpan<byte> page, out uint stored, out uint computed)
    {
        stored = BinaryPrimitives.ReadUInt32LittleEndian(page.Slice(Page.ChecksumFieldOffset, sizeof(uint)));
        computed = 0;
        if (stored == 0)
        {
            return true;
        }

        computed = Compute(page);
        return computed == stored;
    }
}
