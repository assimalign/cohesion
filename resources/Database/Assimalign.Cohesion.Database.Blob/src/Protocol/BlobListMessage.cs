using System;
using System.Collections.Generic;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Blob;

/// <summary>Requests an ordinal prefix listing in the handshake-selected database.</summary>
/// <param name="Container">The ordinal, case-sensitive container name.</param>
/// <param name="Prefix">The ordinal name prefix; empty selects every object.</param>
public sealed record BlobListMessage(string Container, string Prefix = "")
{
    /// <summary>Encodes the container and prefix.</summary>
    /// <returns>The request payload.</returns>
    /// <exception cref="ProtocolException">A string is malformed.</exception>
    public byte[] Encode()
    {
        var buffer = new List<byte>();
        BlobProtocolPayload.WriteString(buffer, Container, allowEmpty: false);
        BlobProtocolPayload.WriteString(buffer, Prefix, allowEmpty: true);
        return buffer.ToArray();
    }

    /// <summary>Decodes the container and prefix.</summary>
    /// <param name="payload">The complete request payload.</param>
    /// <returns>The listing request.</returns>
    /// <exception cref="ProtocolException">The payload is malformed.</exception>
    public static BlobListMessage Decode(ReadOnlySpan<byte> payload)
    {
        int position = 0;
        string container = BlobProtocolPayload.ReadString(payload, ref position, allowEmpty: false);
        string prefix = BlobProtocolPayload.ReadString(payload, ref position, allowEmpty: true);
        BlobProtocolPayload.RequireEnd(payload, position);
        return new(container, prefix);
    }
}
