using System;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http2;

/// <summary>
/// A single unit of inbound request-body data queued from the frame pump to the
/// application's body reader, carrying both the application-visible payload and
/// the flow-control cost of the DATA frame that produced it.
/// </summary>
/// <remarks>
/// RFC 9113 §6.9.1 — the <em>entire</em> DATA frame payload (application data plus
/// any padding and the pad-length octet) counts against the flow-control window,
/// while only the de-padded data is surfaced to the application. The two lengths
/// therefore differ for padded frames, so the credit that must be returned to the
/// peer via <c>WINDOW_UPDATE</c> when the application finishes consuming this chunk
/// is tracked independently in <see cref="FlowControlLength"/> rather than being
/// inferred from <see cref="Data"/>'s length.
/// </remarks>
internal readonly struct Http2DataChunk
{
    /// <summary>
    /// Initializes a new <see cref="Http2DataChunk"/>.
    /// </summary>
    /// <param name="data">The de-padded application data delivered to the reader.</param>
    /// <param name="flowControlLength">
    /// The number of octets to credit back to the stream and connection receive
    /// windows once this chunk has been fully consumed — the full DATA frame
    /// payload length including padding.
    /// </param>
    public Http2DataChunk(ReadOnlyMemory<byte> data, int flowControlLength)
    {
        Data = data;
        FlowControlLength = flowControlLength;
    }

    /// <summary>The application-visible body bytes carried by this chunk.</summary>
    public ReadOnlyMemory<byte> Data { get; }

    /// <summary>
    /// The flow-control octets to credit once <see cref="Data"/> has been fully
    /// read by the application.
    /// </summary>
    public int FlowControlLength { get; }
}
