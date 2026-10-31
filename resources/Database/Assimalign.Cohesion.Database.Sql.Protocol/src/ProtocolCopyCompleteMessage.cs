using System;
using System.Buffers.Binary;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Sql.Protocol;

/// <summary>The verified end of a UTF-8 table export.</summary>
/// <param name="RowCount">The nonnegative data-row count, excluding the header.</param>
/// <param name="ByteCount">The nonnegative number of CopyData bytes.</param>
public sealed record ProtocolCopyCompleteMessage(long RowCount, long ByteCount)
{
    /// <summary>Gets the maximum CopyData payload size.</summary>
    public const int MaximumChunkLength = 16 * 1024;

    /// <summary>Encodes two big-endian Int64 counters.</summary>
    /// <returns>The exact 16-byte payload.</returns>
    /// <exception cref="ProtocolException">A counter is negative or exceeds the possible row count.</exception>
    public byte[] Encode()
    {
        if (RowCount < 0 || ByteCount < 0 || RowCount > ByteCount) { throw new ProtocolException(ErrorMessages.CopyStreamInvalid); }
        var result = new byte[16];
        BinaryPrimitives.WriteInt64BigEndian(result, RowCount);
        BinaryPrimitives.WriteInt64BigEndian(result.AsSpan(8), ByteCount);
        return result;
    }

    /// <summary>Decodes and validates the terminal counters.</summary>
    /// <param name="payload">The exact completion payload.</param>
    /// <returns>The decoded counters.</returns>
    /// <exception cref="ProtocolException">The length or counters are invalid.</exception>
    public static ProtocolCopyCompleteMessage Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 16) { throw new ProtocolException(ErrorMessages.CopyStreamInvalid); }
        long rows = BinaryPrimitives.ReadInt64BigEndian(payload);
        long bytes = BinaryPrimitives.ReadInt64BigEndian(payload[8..]);
        if (rows < 0 || bytes < 0 || rows > bytes) { throw new ProtocolException(ErrorMessages.CopyStreamInvalid); }
        return new(rows, bytes);
    }
}
