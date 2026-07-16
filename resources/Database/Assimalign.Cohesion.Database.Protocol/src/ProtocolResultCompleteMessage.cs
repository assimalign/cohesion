using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Protocol;

/// <summary>
/// Closes an execute exchange: how many records the statement affected
/// (<c>-1</c> for row-returning statements whose count is the row stream itself).
/// </summary>
/// <param name="AffectedCount">The number of affected records, or -1 when not applicable.</param>
public sealed record ProtocolResultCompleteMessage(long AffectedCount)
{
    /// <summary>
    /// Encodes the message payload.
    /// </summary>
    /// <returns>The payload bytes for a <see cref="ProtocolMessageType.ResultComplete"/> frame.</returns>
    public byte[] Encode()
    {
        var buffer = new List<byte>(sizeof(long));
        ProtocolPayload.WriteInt64(buffer, AffectedCount);
        return buffer.ToArray();
    }

    /// <summary>
    /// Decodes a result-complete payload.
    /// </summary>
    /// <param name="payload">The frame payload.</param>
    /// <returns>The decoded message.</returns>
    /// <exception cref="ProtocolException">The payload is malformed.</exception>
    public static ProtocolResultCompleteMessage Decode(ReadOnlySpan<byte> payload)
    {
        int position = 0;
        return new ProtocolResultCompleteMessage(ProtocolPayload.ReadInt64(payload, ref position));
    }
}
