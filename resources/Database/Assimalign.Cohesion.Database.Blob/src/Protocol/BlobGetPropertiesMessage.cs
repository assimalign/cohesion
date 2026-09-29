using System;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Blob;

/// <summary>Requests object properties in the handshake-selected database.</summary>
/// <param name="Container">The ordinal, case-sensitive container name.</param>
/// <param name="Name">The ordinal, case-sensitive object name.</param>
public sealed record BlobGetPropertiesMessage(string Container, string Name)
{
    /// <summary>Encodes the object identity.</summary>
    /// <returns>The request payload.</returns>
    /// <exception cref="ProtocolException">An identity is malformed.</exception>
    public byte[] Encode() => new BlobReadMessage(Container, Name).Encode();

    /// <summary>Decodes an object identity.</summary>
    /// <param name="payload">The complete request payload.</param>
    /// <returns>The property request.</returns>
    /// <exception cref="ProtocolException">The payload is malformed.</exception>
    public static BlobGetPropertiesMessage Decode(ReadOnlySpan<byte> payload)
    {
        var identity = BlobReadMessage.Decode(payload);
        return new(identity.Container, identity.Name);
    }
}
