using System;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Blob;

/// <summary>Acknowledges one chunk after the receiver has written it to its destination.</summary>
/// <param name="Length">The cumulative number of accepted content bytes.</param>
public readonly record struct BlobChunkAcknowledgementMessage(long Length)
{
    /// <summary>Encodes a Blob ChunkAcknowledgement payload.</summary>
    /// <returns>The eight-byte big-endian cumulative count.</returns>
    /// <exception cref="ProtocolException">The byte count is negative.</exception>
    public byte[] Encode() => new BlobTransferCompleteMessage(Length).Encode();

    /// <summary>Decodes a Blob ChunkAcknowledgement payload.</summary>
    /// <param name="payload">The complete frame payload.</param>
    /// <returns>The cumulative accepted byte count.</returns>
    /// <exception cref="ProtocolException">The payload is malformed.</exception>
    public static BlobChunkAcknowledgementMessage Decode(ReadOnlySpan<byte> payload)
        => new(BlobTransferCompleteMessage.Decode(payload).Length);
}
