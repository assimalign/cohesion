namespace Assimalign.Cohesion.Database.Blob;

/// <summary>Message identifiers scoped to a Blob endpoint after the shared handshake.</summary>
public enum BlobProtocolMessageType : byte
{
    /// <summary>Requests the content of a named object.</summary>
    Read = 64,

    /// <summary>Requests an upload to a named object.</summary>
    Write = 65,

    /// <summary>Introduces the content length and content type of a transfer.</summary>
    TransferStart = 66,

    /// <summary>Carries one bounded, nonempty portion of object content.</summary>
    Chunk = 67,

    /// <summary>Completes a content transfer or acknowledges a published upload.</summary>
    TransferComplete = 68,

    /// <summary>Acknowledges the cumulative content count after accepting one chunk.</summary>
    ChunkAcknowledgement = 69,
}
