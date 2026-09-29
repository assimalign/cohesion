using System;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Blob;

/// <summary>A nonempty content chunk, retaining its caller-owned buffer without copying.</summary>
/// <param name="Content">The bytes, whose lifetime must cover the awaited frame write.</param>
public readonly record struct BlobChunkMessage(ReadOnlyMemory<byte> Content)
{
    /// <summary>Wraps the content as a bounded Blob Chunk frame without copying.</summary>
    /// <returns>The frame referencing this content.</returns>
    /// <exception cref="ProtocolException">The content length is outside 1 through 65,536 bytes.</exception>
    public ProtocolFrame ToFrame()
    {
        ValidateLength(Content.Length);
        return new((ProtocolMessageType)BlobProtocolMessageType.Chunk, Content);
    }

    /// <summary>Decodes a Blob Chunk payload without copying.</summary>
    /// <param name="payload">The complete frame payload.</param>
    /// <returns>The content chunk referencing the supplied payload.</returns>
    /// <exception cref="ProtocolException">The content length is outside 1 through 65,536 bytes.</exception>
    public static BlobChunkMessage Decode(ReadOnlyMemory<byte> payload)
    {
        ValidateLength(payload.Length);
        return new(payload);
    }

    private static void ValidateLength(int length)
    {
        if (length == 0 || length > BlobProtocol.MaxChunkLength)
        {
            throw new ProtocolException($"A Blob chunk must contain 1 through {BlobProtocol.MaxChunkLength} bytes.");
        }
    }
}
