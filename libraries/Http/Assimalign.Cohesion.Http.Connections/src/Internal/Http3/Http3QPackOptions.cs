namespace Assimalign.Cohesion.Http.Connections.Internal.Http3;

/// <summary>
/// The server-side QPACK configuration (RFC 9204 §5) that the HTTP/3 transport
/// advertises in SETTINGS and enforces while decoding request field sections:
/// the decoder's dynamic table capacity (<c>QPACK_MAX_TABLE_CAPACITY</c>) and
/// how many streams may block on pending insertions
/// (<c>QPACK_BLOCKED_STREAMS</c>).
/// </summary>
/// <remarks>
/// The default is the standards-blessed static-only profile
/// (<see cref="StaticOnly"/>): capacity 0 disables the dynamic table entirely,
/// and the transport behaves exactly as it did before opt-in dynamic-table
/// support existed. Setting <see cref="MaxTableCapacity"/> above 0 opts in to the
/// dynamic table, the encoder/decoder instruction streams, and blocked-stream
/// bookkeeping.
/// </remarks>
internal sealed class Http3QPackOptions
{
    /// <summary>
    /// Gets the static-only default (dynamic table disabled): the profile the
    /// transport uses unless a deployment opts in to a non-zero capacity.
    /// </summary>
    public static Http3QPackOptions StaticOnly { get; } = new();

    /// <summary>
    /// Gets the decoder's advertised <c>QPACK_MAX_TABLE_CAPACITY</c> in octets.
    /// A value of 0 (the default) disables the dynamic table.
    /// </summary>
    public long MaxTableCapacity { get; init; }

    /// <summary>
    /// Gets the decoder's advertised <c>QPACK_BLOCKED_STREAMS</c> — the maximum
    /// number of streams permitted to block on not-yet-received insertions
    /// (RFC 9204 §2.1.2). Ignored when the dynamic table is disabled.
    /// </summary>
    public long MaxBlockedStreams { get; init; }

    /// <summary>
    /// Gets whether the dynamic table is enabled (<see cref="MaxTableCapacity"/> &gt; 0).
    /// </summary>
    public bool DynamicTableEnabled => MaxTableCapacity > 0;
}
