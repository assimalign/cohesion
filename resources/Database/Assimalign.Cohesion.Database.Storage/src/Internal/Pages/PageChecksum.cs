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
        uint checksum = Crc32C.Finalize(state);
        // Zero is reserved for wholly zero pages in storage format 4. Escape a real
        // CRC of zero so a legitimate nonzero page always has a stored checksum.
        // This aliases raw CRC 0 and 0xFFFFFFFF; it is not collision-free. PostgreSQL also
        // reduces its checksum space to avoid zero (src/include/storage/checksum_impl.h:196-199).
        return checksum == 0 ? uint.MaxValue : checksum;
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
    /// A stored checksum of zero is accepted only for an entirely zero-filled page.
    /// Every nonzero page must carry its checksum.
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
    /// throwing: true when the checksum matches or the entire page is zero.
    /// </summary>
    internal static bool TryVerify(ReadOnlySpan<byte> page, out uint stored, out uint computed)
    {
        stored = BinaryPrimitives.ReadUInt32LittleEndian(page.Slice(Page.ChecksumFieldOffset, sizeof(uint)));
        computed = 0;
        if (stored == 0 && page.IndexOfAnyExcept((byte)0) < 0)
        {
            return true;
        }

        computed = Compute(page);
        return computed == stored;
    }
}
