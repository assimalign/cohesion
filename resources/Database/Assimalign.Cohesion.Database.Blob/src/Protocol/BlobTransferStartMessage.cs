using System;
using System.Collections.Generic;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Blob;

/// <summary>Begins an upload or download without requiring a known content length.</summary>
/// <param name="Length">The expected byte count, or -1 when unknown until completion.</param>
/// <param name="ContentType">The content type; an empty string means unspecified.</param>
public sealed record BlobTransferStartMessage(long Length = -1, string ContentType = "")
{
    /// <summary>Encodes a Blob TransferStart payload.</summary>
    /// <returns>The payload bytes.</returns>
    /// <exception cref="ProtocolException">The length or content type is invalid.</exception>
    public byte[] Encode()
    {
        if (Length < -1)
        {
            throw new ProtocolException("A Blob transfer length must be nonnegative or -1.");
        }
        var buffer = new List<byte>();
        BlobProtocolPayload.WriteInt64(buffer, Length);
        BlobProtocolPayload.WriteString(buffer, ContentType, allowEmpty: true);
        return buffer.ToArray();
    }

    /// <summary>Decodes a Blob TransferStart payload.</summary>
    /// <param name="payload">The complete frame payload.</param>
    /// <returns>The transfer metadata.</returns>
    /// <exception cref="ProtocolException">The payload is malformed.</exception>
    public static BlobTransferStartMessage Decode(ReadOnlySpan<byte> payload)
    {
        int position = 0;
        long length = BlobProtocolPayload.ReadInt64(payload, ref position);
        if (length < -1)
        {
            throw new ProtocolException("A Blob transfer length must be nonnegative or -1.");
        }
        string contentType = BlobProtocolPayload.ReadString(payload, ref position, allowEmpty: true);
        BlobProtocolPayload.RequireEnd(payload, position);
        return new(length, contentType);
    }
}
