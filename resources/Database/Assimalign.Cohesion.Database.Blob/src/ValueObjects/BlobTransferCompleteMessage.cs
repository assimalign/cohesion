using System;
using System.Buffers.Binary;
using Assimalign.Cohesion.Database.Blob.Internal;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Blob;

/// <summary>Ends a transfer with the actual byte count, or acknowledges a published upload.</summary>
/// <param name="Length">The actual number of content bytes transferred.</param>
public readonly record struct BlobTransferCompleteMessage(long Length)
{
    /// <summary>Encodes a Blob TransferComplete payload.</summary>
    /// <returns>The payload bytes.</returns>
    /// <exception cref="ProtocolException">The byte count is negative.</exception>
    public byte[] Encode()
    {
        if (Length < 0)
        {
            throw new ProtocolException("A completed Blob transfer length must be nonnegative.");
        }
        var payload = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(payload, Length);
        return payload;
    }

    /// <summary>Decodes a Blob TransferComplete payload.</summary>
    /// <param name="payload">The complete frame payload.</param>
    /// <returns>The actual byte count.</returns>
    /// <exception cref="ProtocolException">The payload is malformed.</exception>
    public static BlobTransferCompleteMessage Decode(ReadOnlySpan<byte> payload)
    {
        int position = 0;
        long length = BlobProtocolPayload.ReadInt64(payload, ref position);
        BlobProtocolPayload.RequireEnd(payload, position);
        if (length < 0)
        {
            throw new ProtocolException("A completed Blob transfer length must be nonnegative.");
        }
        return new(length);
    }
}
