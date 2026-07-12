using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Protocol;

/// <summary>
/// The first message of every connection: the client's protocol version, the
/// database it wants, and the principal it claims (authentication proof follows in
/// the Authenticate exchange).
/// </summary>
/// <param name="Version">The client's protocol version. Servers reject unknown majors with <see cref="ProtocolErrorCode.UnsupportedVersion"/>.</param>
/// <param name="Database">The database name to bind the session to.</param>
/// <param name="Principal">The principal name the client claims.</param>
public sealed record ProtocolStartupMessage(ProtocolVersion Version, string Database, string Principal)
{
    /// <summary>
    /// Encodes the message payload.
    /// </summary>
    /// <returns>The payload bytes for a <see cref="ProtocolMessageType.Startup"/> frame.</returns>
    public byte[] Encode()
    {
        var buffer = new List<byte>(64);
        ProtocolPayload.WriteUInt16(buffer, Version.Major);
        ProtocolPayload.WriteUInt16(buffer, Version.Minor);
        ProtocolPayload.WriteString(buffer, Database);
        ProtocolPayload.WriteString(buffer, Principal);
        return buffer.ToArray();
    }

    /// <summary>
    /// Decodes a startup payload.
    /// </summary>
    /// <param name="payload">The frame payload.</param>
    /// <returns>The decoded message.</returns>
    /// <exception cref="ProtocolException">The payload is malformed.</exception>
    public static ProtocolStartupMessage Decode(ReadOnlySpan<byte> payload)
    {
        int position = 0;
        ushort major = ProtocolPayload.ReadUInt16(payload, ref position);
        ushort minor = ProtocolPayload.ReadUInt16(payload, ref position);
        string database = ProtocolPayload.ReadString(payload, ref position);
        string principal = ProtocolPayload.ReadString(payload, ref position);
        return new ProtocolStartupMessage(new ProtocolVersion(major, minor), database, principal);
    }
}
