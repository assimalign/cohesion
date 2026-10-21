using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Documents.Protocol.Internal;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Documents.Protocol;

/// <summary>A collection visible in the bound database.</summary>
/// <param name="Name">The ordinal collection name.</param>
public sealed record DocumentProtocolCollectionMessage(string Name)
{
    /// <summary>Encodes the collection name.</summary>
    /// <returns>The payload.</returns>
    /// <exception cref="ProtocolException">The name is blank.</exception>
    public byte[] Encode()
    {
        if (string.IsNullOrWhiteSpace(Name)) { throw new ProtocolException(ErrorMessages.CollectionNameMissing); }
        var buffer = new List<byte>();
        ProtocolPayload.WriteString(buffer, Name);
        return buffer.ToArray();
    }

    /// <summary>Decodes the collection name.</summary>
    /// <param name="payload">The complete frame payload.</param>
    /// <returns>The collection name.</returns>
    /// <exception cref="ProtocolException">The payload is invalid.</exception>
    public static DocumentProtocolCollectionMessage Decode(ReadOnlySpan<byte> payload)
    {
        int position = 0;
        string name = ProtocolPayload.ReadString(payload, ref position);
        DocumentProtocolWire.EnsureEnd(payload, position);
        if (string.IsNullOrWhiteSpace(name)) { throw new ProtocolException(ErrorMessages.CollectionNameMissing); }
        return new(name);
    }
}
