using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Graph;

/// <summary>Terminates a successful path exchange.</summary>
/// <param name="PathCount">The number of path frames sent in the exchange.</param>
public sealed record GraphProtocolPathsCompleteMessage(long PathCount)
{
    /// <summary>Encodes the successful path count.</summary>
    /// <returns>The eight-byte big-endian count.</returns>
    /// <exception cref="ProtocolException">The path count is negative.</exception>
    public byte[] Encode()
    {
        if (PathCount < 0) { throw new ProtocolException("A path count cannot be negative."); }
        var buffer = new List<byte>(8);
        ProtocolPayload.WriteInt64(buffer, PathCount);
        return buffer.ToArray();
    }

    /// <summary>Decodes the successful path count.</summary>
    /// <param name="payload">The frame payload.</param>
    /// <returns>The decoded path count.</returns>
    /// <exception cref="ProtocolException">The payload is not exactly eight bytes or the count is negative.</exception>
    public static GraphProtocolPathsCompleteMessage Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 8) { throw new ProtocolException("Malformed path completion payload."); }
        int position = 0;
        long count = ProtocolPayload.ReadInt64(payload, ref position);
        if (count < 0) { throw new ProtocolException("A path count cannot be negative."); }
        return new(count);
    }
}
