using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Documents;

/// <summary>Terminates a successful document exchange.</summary>
/// <param name="ResultCount">The number of row, document or collection frames sent in the exchange.</param>
/// <param name="AffectedCount">The write count, or -1 for queries and reads.</param>
public sealed record DocumentProtocolCompleteMessage(long ResultCount, long AffectedCount)
{
    /// <summary>Encodes the successful result and affected counts.</summary>
    /// <returns>The two eight-byte big-endian counts.</returns>
    /// <exception cref="ProtocolException">A count is outside its range.</exception>
    public byte[] Encode()
    {
        if (ResultCount < 0 || AffectedCount < -1) { throw new ProtocolException(ErrorMessages.CompletionCountsInvalid); }
        var buffer = new List<byte>(16);
        ProtocolPayload.WriteInt64(buffer, ResultCount);
        ProtocolPayload.WriteInt64(buffer, AffectedCount);
        return buffer.ToArray();
    }

    /// <summary>Decodes the successful result and affected counts.</summary>
    /// <param name="payload">The frame payload.</param>
    /// <returns>The decoded result and affected counts.</returns>
    /// <exception cref="ProtocolException">The payload is not exactly sixteen bytes or a count is invalid.</exception>
    public static DocumentProtocolCompleteMessage Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 16) { throw new ProtocolException(ErrorMessages.CompletionPayloadInvalid); }
        int position = 0;
        long count = ProtocolPayload.ReadInt64(payload, ref position);
        long affected = ProtocolPayload.ReadInt64(payload, ref position);
        if (count < 0 || affected < -1) { throw new ProtocolException(ErrorMessages.CompletionCountsInvalid); }
        return new(count, affected);
    }
}
