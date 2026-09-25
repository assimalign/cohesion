using System;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Blob;

/// <summary>Completes a metadata operation with a nonnegative result count.</summary>
/// <param name="Count">Zero or one for delete/property read, or the number of listed objects.</param>
public readonly record struct BlobOperationCompleteMessage(long Count)
{
    /// <summary>Encodes the result count.</summary>
    /// <returns>The eight-byte count.</returns>
    /// <exception cref="ProtocolException">The count is negative.</exception>
    public byte[] Encode() => new BlobTransferCompleteMessage(Count).Encode();

    /// <summary>Decodes the result count.</summary>
    /// <param name="payload">The complete completion payload.</param>
    /// <returns>The operation result count.</returns>
    /// <exception cref="ProtocolException">The payload is malformed.</exception>
    public static BlobOperationCompleteMessage Decode(ReadOnlySpan<byte> payload)
        => new(BlobTransferCompleteMessage.Decode(payload).Length);
}
