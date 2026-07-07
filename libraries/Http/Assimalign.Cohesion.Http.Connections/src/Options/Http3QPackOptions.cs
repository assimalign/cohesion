using System;

namespace Assimalign.Cohesion.Http.Connections;

/// <summary>
/// The HTTP/3 QPACK decoder configuration (RFC 9204 §5): the dynamic table
/// capacity and blocked-stream tolerance the server advertises in SETTINGS and
/// enforces while decoding request field sections.
/// </summary>
/// <remarks>
/// The default is the standards-blessed static-only profile — capacity 0
/// disables the dynamic table entirely, and the transport behaves exactly as it
/// did before opt-in dynamic-table support existed. Set
/// <see cref="MaxTableCapacity"/> above 0 to opt in to the dynamic table, the
/// encoder/decoder instruction streams, and blocked-stream bookkeeping.
/// </remarks>
public sealed class Http3QPackOptions
{
    private long _maxTableCapacity;
    private long _maxBlockedStreams;

    /// <summary>
    /// Gets or sets the decoder's advertised <c>QPACK_MAX_TABLE_CAPACITY</c> in
    /// octets. A value of 0 (the default) disables the dynamic table.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when set to a negative value.</exception>
    public long MaxTableCapacity
    {
        get => _maxTableCapacity;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _maxTableCapacity = value;
        }
    }

    /// <summary>
    /// Gets or sets the decoder's advertised <c>QPACK_BLOCKED_STREAMS</c> — the
    /// maximum number of streams permitted to block on not-yet-received
    /// insertions (RFC 9204 §2.1.2). Ignored when the dynamic table is disabled.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when set to a negative value.</exception>
    public long MaxBlockedStreams
    {
        get => _maxBlockedStreams;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _maxBlockedStreams = value;
        }
    }

    /// <summary>
    /// Gets whether the dynamic table is enabled (<see cref="MaxTableCapacity"/> &gt; 0).
    /// </summary>
    internal bool DynamicTableEnabled => _maxTableCapacity > 0;
}
