using System.IO;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http3.QPack;

/// <summary>
/// Encodes the QPACK decoder-stream instructions the decoder sends back to the
/// peer's encoder (RFC 9204 §4.4): Section Acknowledgment (§4.4.1), Stream
/// Cancellation (§4.4.2), and Insert Count Increment (§4.4.3). Each is a single
/// prefixed integer with a representation-pattern high bit.
/// </summary>
internal static class QPackDecoderInstructionEncoder
{
    // Representation pattern bits (RFC 9204 §4.4).
    private const byte SectionAcknowledgmentPattern = 0b1000_0000; // §4.4.1: 1 + 7-bit stream id.
    private const byte StreamCancellationPattern = 0b0100_0000;    // §4.4.2: 0 1 + 6-bit stream id.
    private const byte InsertCountIncrementPattern = 0b0000_0000;  // §4.4.3: 0 0 + 6-bit increment.

    /// <summary>
    /// Encodes a Section Acknowledgment (RFC 9204 §4.4.1) for the request stream
    /// on which a dynamic-table-referencing field section was decoded.
    /// </summary>
    /// <param name="streamId">The request stream identifier.</param>
    /// <returns>The encoded instruction octets.</returns>
    public static byte[] SectionAcknowledgment(long streamId)
    {
        using MemoryStream buffer = new();
        QPackPrefixedInteger.Encode(buffer, streamId, 7, SectionAcknowledgmentPattern);
        return buffer.ToArray();
    }

    /// <summary>
    /// Encodes a Stream Cancellation (RFC 9204 §4.4.2) for a request stream that
    /// was reset or abandoned before its field section was acknowledged.
    /// </summary>
    /// <param name="streamId">The cancelled request stream identifier.</param>
    /// <returns>The encoded instruction octets.</returns>
    public static byte[] StreamCancellation(long streamId)
    {
        using MemoryStream buffer = new();
        QPackPrefixedInteger.Encode(buffer, streamId, 6, StreamCancellationPattern);
        return buffer.ToArray();
    }

    /// <summary>
    /// Encodes an Insert Count Increment (RFC 9204 §4.4.3) acknowledging that
    /// <paramref name="increment"/> further insertions have been processed from
    /// the encoder stream.
    /// </summary>
    /// <param name="increment">The number of newly processed insertions (must be &gt; 0).</param>
    /// <returns>The encoded instruction octets.</returns>
    public static byte[] InsertCountIncrement(long increment)
    {
        using MemoryStream buffer = new();
        QPackPrefixedInteger.Encode(buffer, increment, 6, InsertCountIncrementPattern);
        return buffer.ToArray();
    }
}
