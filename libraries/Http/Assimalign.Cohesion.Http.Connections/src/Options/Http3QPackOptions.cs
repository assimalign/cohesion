using System;

namespace Assimalign.Cohesion.Http.Connections;

/// <summary>
/// The HTTP/3 QPACK decoder configuration (RFC 9204 §5): the dynamic table
/// capacity, the blocked-stream tolerance, and the decoded field-section size
/// the server advertises in SETTINGS and enforces while decoding request field
/// sections.
/// </summary>
/// <remarks>
/// The default is the standards-blessed static-only profile — capacity 0
/// disables the dynamic table entirely, and the transport behaves exactly as it
/// did before opt-in dynamic-table support existed. Set
/// <see cref="MaxTableCapacity"/> above 0 to opt in to the dynamic table, the
/// encoder/decoder instruction streams, and blocked-stream bookkeeping.
/// <see cref="MaxFieldSectionSize"/> applies on both profiles.
/// </remarks>
public sealed class Http3QPackOptions
{
    /// <summary>
    /// Default <see cref="MaxFieldSectionSize"/>: 16 KB, the HTTP/2 transport's default
    /// <c>SETTINGS_MAX_HEADER_LIST_SIZE</c>, which RFC 9113 counts the same way.
    /// </summary>
    public const long DefaultMaxFieldSectionSize = 16 * 1024;

    // The largest value a SETTINGS parameter can carry: a QUIC variable-length integer (RFC 9000 §16).
    private const long maxSettingValue = (1L << 62) - 1;

    private long _maxTableCapacity;
    private long _maxBlockedStreams;
    private long _maxFieldSectionSize = DefaultMaxFieldSectionSize;

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
    /// Gets or sets the largest decoded field section, in octets, the server accepts on a request
    /// stream: the request's header section or its trailer section. Each field counts the length of its
    /// name and its value plus 32 octets (RFC 9114 §4.2.2), so a section of many small fields reaches
    /// the limit as surely as one of a few large ones. Defaults to
    /// <see cref="DefaultMaxFieldSectionSize"/> (16 KB).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The server advertises the value as <c>SETTINGS_MAX_FIELD_SECTION_SIZE</c> (RFC 9114 §7.2.4.1)
    /// and checks it inside the QPACK decoder, before each field joins the decoded list. A small encoded
    /// section that expands, such as one-octet references to the static table or references to a large
    /// dynamic-table entry, therefore stops at the limit instead of being decoded whole.
    /// <see cref="Http3ConnectionListenerOptions.Http3Limits.MaxRequestHeadersFrameSize"/> bounds the
    /// encoded HEADERS frame; this bounds what it decodes to.
    /// </para>
    /// <para>
    /// A request whose header section exceeds the limit is answered <c>431 Request Header Fields Too
    /// Large</c> without being dispatched. A trailer section that exceeds it fails the request-body read;
    /// the exchange is then answered 431 if its response has not started, and otherwise the request
    /// stream is reset with <c>H3_MESSAGE_ERROR</c>. The connection and its other streams are unaffected.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when set to a value less than 1, or greater than 2^62 - 1, the largest value a SETTINGS
    /// parameter can carry.
    /// </exception>
    public long MaxFieldSectionSize
    {
        get => _maxFieldSectionSize;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, maxSettingValue);
            _maxFieldSectionSize = value;
        }
    }

    /// <summary>
    /// Gets whether the dynamic table is enabled (<see cref="MaxTableCapacity"/> &gt; 0).
    /// </summary>
    internal bool DynamicTableEnabled => _maxTableCapacity > 0;

    /// <summary>
    /// Copies the options. A connection takes a copy when it opens, so the SETTINGS it advertises and the
    /// limits it enforces come from one set of values for its whole life, even if the listener's instance
    /// changes afterwards.
    /// </summary>
    /// <returns>A copy of the options.</returns>
    internal Http3QPackOptions Snapshot()
    {
        return new Http3QPackOptions
        {
            _maxTableCapacity = _maxTableCapacity,
            _maxBlockedStreams = _maxBlockedStreams,
            _maxFieldSectionSize = _maxFieldSectionSize,
        };
    }
}
