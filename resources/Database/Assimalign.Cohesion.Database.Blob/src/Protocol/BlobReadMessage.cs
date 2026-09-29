using System;
using System.Collections.Generic;
using Assimalign.Cohesion.Database.Blob.Internal;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Blob;

/// <summary>Requests a download from a container in the handshake-selected database.</summary>
/// <param name="Container">The ordinal, case-sensitive container name.</param>
/// <param name="Name">The ordinal, case-sensitive object name.</param>
public sealed record BlobReadMessage(string Container, string Name)
{
    /// <summary>Encodes a Blob Read payload.</summary>
    /// <returns>The payload bytes.</returns>
    /// <exception cref="ProtocolException">A name is empty, null, oversized, or invalid Unicode.</exception>
    public byte[] Encode()
    {
        var buffer = new List<byte>();
        BlobProtocolPayload.WriteString(buffer, Container, allowEmpty: false);
        BlobProtocolPayload.WriteString(buffer, Name, allowEmpty: false);
        return buffer.ToArray();
    }

    /// <summary>Decodes a Blob Read payload.</summary>
    /// <param name="payload">The complete frame payload.</param>
    /// <returns>The requested object identity.</returns>
    /// <exception cref="ProtocolException">The payload is malformed.</exception>
    public static BlobReadMessage Decode(ReadOnlySpan<byte> payload)
    {
        int position = 0;
        string container = BlobProtocolPayload.ReadString(payload, ref position, allowEmpty: false);
        string name = BlobProtocolPayload.ReadString(payload, ref position, allowEmpty: false);
        BlobProtocolPayload.RequireEnd(payload, position);
        return new(container, name);
    }
}
