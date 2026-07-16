using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Protocol;

/// <summary>
/// The error frame terminating any exchange: a stable wire error code plus a
/// human-readable message.
/// </summary>
/// <param name="Code">The stable error code (wire contract; values are append-only).</param>
/// <param name="Message">The human-readable error message.</param>
public sealed record ProtocolErrorMessage(ProtocolErrorCode Code, string Message)
{
    /// <summary>
    /// Encodes the message payload.
    /// </summary>
    /// <returns>The payload bytes for a <see cref="ProtocolMessageType.Error"/> frame.</returns>
    public byte[] Encode()
    {
        var buffer = new List<byte>(32);
        ProtocolPayload.WriteUInt16(buffer, (ushort)Code);
        ProtocolPayload.WriteString(buffer, Message);
        return buffer.ToArray();
    }

    /// <summary>
    /// Decodes an error payload.
    /// </summary>
    /// <param name="payload">The frame payload.</param>
    /// <returns>The decoded message.</returns>
    /// <exception cref="ProtocolException">The payload is malformed.</exception>
    public static ProtocolErrorMessage Decode(ReadOnlySpan<byte> payload)
    {
        int position = 0;
        var code = (ProtocolErrorCode)ProtocolPayload.ReadUInt16(payload, ref position);
        string message = ProtocolPayload.ReadString(payload, ref position);
        return new ProtocolErrorMessage(code, message);
    }
}
