using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Documents;

/// <summary>A stored document's identity, version and exact content bytes.</summary>
/// <param name="Document">The stored document.</param>
public sealed record DocumentProtocolStoredDocumentMessage(Document Document)
{
    /// <summary>Encodes the stored document.</summary>
    /// <returns>The complete payload.</returns>
    /// <exception cref="ProtocolException">The identity or JSON is invalid.</exception>
    public byte[] Encode()
    {
        if (string.IsNullOrWhiteSpace(Document.Id.Value)) { throw new ProtocolException(ErrorMessages.DocumentIdentityMissing); }
        DocumentProtocolJson.Validate(Document.Content.Span);
        var buffer = new List<byte>();
        ProtocolPayload.WriteString(buffer, Document.Id.Value);
        ProtocolPayload.WriteInt64(buffer, unchecked((long)Document.Version.Value));
        ProtocolPayload.WriteInt32(buffer, Document.Content.Length);
        buffer.AddRange(Document.Content.ToArray());
        return buffer.ToArray();
    }

    /// <summary>Decodes a stored document, preserving its content bytes.</summary>
    /// <param name="payload">The complete frame payload.</param>
    /// <returns>The stored document with owned content.</returns>
    /// <exception cref="ProtocolException">The payload is invalid.</exception>
    public static DocumentProtocolStoredDocumentMessage Decode(ReadOnlySpan<byte> payload)
    {
        int position = 0;
        string id = ProtocolPayload.ReadString(payload, ref position);
        if (string.IsNullOrWhiteSpace(id)) { throw new ProtocolException(ErrorMessages.DocumentIdentityMissing); }
        var version = new DocumentVersion(unchecked((ulong)ProtocolPayload.ReadInt64(payload, ref position)));
        int length = ProtocolPayload.ReadInt32(payload, ref position);
        if (length < 0 || length != payload.Length - position) { throw new ProtocolException(ErrorMessages.ContentLengthInvalid); }
        DocumentProtocolJson.Validate(payload[position..]);
        return new(new Document(id, version, payload[position..].ToArray()));
    }
}
