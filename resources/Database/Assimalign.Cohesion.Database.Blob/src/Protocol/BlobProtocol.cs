using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Blob;

/// <summary>Defines the Blob message family and its bounded content payload size.</summary>
public static class BlobProtocol
{
    /// <summary>The largest content chunk, in bytes, in the version 1 Blob family.</summary>
    public const int MaxChunkLength = 64 * 1024;

    /// <summary>The immutable family selected when accepting or opening a Blob connection.</summary>
    public static ProtocolMessageFamily Family { get; } = new("Blob",
        (byte)BlobProtocolMessageType.Read,
        (byte)BlobProtocolMessageType.Write,
        (byte)BlobProtocolMessageType.TransferStart,
        (byte)BlobProtocolMessageType.Chunk,
        (byte)BlobProtocolMessageType.TransferComplete,
        (byte)BlobProtocolMessageType.ChunkAcknowledgement,
        (byte)BlobProtocolMessageType.Delete,
        (byte)BlobProtocolMessageType.GetProperties,
        (byte)BlobProtocolMessageType.List,
        (byte)BlobProtocolMessageType.Properties,
        (byte)BlobProtocolMessageType.OperationComplete);
}
