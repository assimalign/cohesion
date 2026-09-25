using System;

using Assimalign.Cohesion.Database.Documents.Internal;

namespace Assimalign.Cohesion.Database.Documents;

/// <summary>One document result preserving its complete JSON shape and original UTF-8 bytes.</summary>
/// <param name="Json">The UTF-8 JSON value, including an object, array, scalar, or null.</param>
public sealed record DocumentProtocolResultMessage(ReadOnlyMemory<byte> Json)
{
    /// <summary>Validates and copies the complete JSON result into a frame payload.</summary>
    /// <returns>The JSON payload bytes.</returns>
    /// <exception cref="Protocol.ProtocolException">The JSON value is malformed or exceeds 256 levels.</exception>
    public byte[] Encode()
    {
        DocumentProtocolJson.Validate(Json.Span);
        return Json.ToArray();
    }

    /// <summary>Decodes a complete JSON result without flattening it.</summary>
    /// <param name="payload">The frame payload.</param>
    /// <returns>The result with its own copy of the original JSON bytes.</returns>
    /// <exception cref="Protocol.ProtocolException">The JSON value is malformed or exceeds 256 levels.</exception>
    public static DocumentProtocolResultMessage Decode(ReadOnlySpan<byte> payload)
    {
        DocumentProtocolJson.Validate(payload);
        return new(payload.ToArray());
    }
}
