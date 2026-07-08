namespace Assimalign.Cohesion.Connections;

/// <summary>
/// An optional capability a stream of a multiplexed transport may expose to
/// surface the transport-native identifier the peer assigned to it — for QUIC,
/// the 62-bit stream ID (RFC 9000 §2.1).
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately <em>not</em> part of the general <see cref="IConnection"/>
/// contract: a byte-stream transport (such as TCP) has no wire-level stream number,
/// and the synthetic <see cref="IConnection.Id"/> is an opaque per-process value, not
/// a wire identifier. A consumer that needs the wire identifier — a QPACK decoder that
/// keys Section Acknowledgment / Stream Cancellation on the request stream ID (RFC 9204
/// §4.4), for example — queries for this capability with a type test and falls back when
/// it is absent, so the identifier never leaks onto the general connection surface.
/// </para>
/// <para>
/// The value is only meaningful for the lifetime of the stream it identifies and is
/// unique within its owning multiplexed connection, not across connections.
/// </para>
/// </remarks>
public interface IStreamIdentifierFeature
{
    /// <summary>
    /// Gets the transport-native identifier of the underlying stream (for QUIC, the
    /// 62-bit stream ID of RFC 9000 §2.1).
    /// </summary>
    long StreamId { get; }
}
