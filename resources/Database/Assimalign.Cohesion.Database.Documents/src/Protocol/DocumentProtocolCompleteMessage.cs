using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Documents;

/// <summary>Terminates a successful document exchange.</summary>
/// <param name="ResultCount">The number of document result frames sent in the exchange.</param>
public sealed record DocumentProtocolCompleteMessage(long ResultCount)
{
    /// <summary>Encodes the successful result count.</summary>
    /// <returns>The eight-byte big-endian count.</returns>
    /// <exception cref="ProtocolException">The result count is negative.</exception>
    public byte[] Encode()
    {
        if (ResultCount < 0) { throw new ProtocolException("A document result count cannot be negative."); }
        var buffer = new List<byte>(8);
        ProtocolPayload.WriteInt64(buffer, ResultCount);
        return buffer.ToArray();
    }

    /// <summary>Decodes the successful result count.</summary>
    /// <param name="payload">The frame payload.</param>
    /// <returns>The decoded result count.</returns>
    /// <exception cref="ProtocolException">The payload is not exactly eight bytes or the count is negative.</exception>
    public static DocumentProtocolCompleteMessage Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 8) { throw new ProtocolException("Malformed document completion payload."); }
        int position = 0;
        long count = ProtocolPayload.ReadInt64(payload, ref position);
        if (count < 0) { throw new ProtocolException("A document result count cannot be negative."); }
        return new(count);
    }
}
