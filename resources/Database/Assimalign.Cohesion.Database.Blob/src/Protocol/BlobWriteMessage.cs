using System;
using System.Collections.Generic;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Blob;

/// <summary>Requests an upload to a container in the handshake-selected database.</summary>
/// <param name="Container">The ordinal, case-sensitive container name.</param>
/// <param name="Name">The ordinal, case-sensitive object name.</param>
/// <param name="Overwrite">Whether an existing object may be replaced.</param>
public sealed record BlobWriteMessage(string Container, string Name, bool Overwrite = true)
{
    /// <summary>Encodes a Blob Write payload.</summary>
    /// <returns>The payload bytes.</returns>
    /// <exception cref="ProtocolException">A name is empty, null, oversized, or invalid Unicode.</exception>
    public byte[] Encode()
    {
        var buffer = new List<byte>();
        BlobProtocolPayload.WriteString(buffer, Container, allowEmpty: false);
        BlobProtocolPayload.WriteString(buffer, Name, allowEmpty: false);
        buffer.Add(Overwrite ? (byte)1 : (byte)0);
        return buffer.ToArray();
    }

    /// <summary>Decodes a Blob Write payload.</summary>
    /// <param name="payload">The complete frame payload.</param>
    /// <returns>The upload request.</returns>
    /// <exception cref="ProtocolException">The payload is malformed.</exception>
    public static BlobWriteMessage Decode(ReadOnlySpan<byte> payload)
    {
        int position = 0;
        string container = BlobProtocolPayload.ReadString(payload, ref position, allowEmpty: false);
        string name = BlobProtocolPayload.ReadString(payload, ref position, allowEmpty: false);
        if (payload.Length - position != 1 || payload[position] > 1)
        {
            throw new ProtocolException("Malformed Blob Write overwrite flag.");
        }
        return new(container, name, payload[position] == 1);
    }
}
