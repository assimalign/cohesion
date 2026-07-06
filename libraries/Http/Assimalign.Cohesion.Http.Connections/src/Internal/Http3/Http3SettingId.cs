namespace Assimalign.Cohesion.Http.Connections.Internal.Http3;

/// <summary>
/// HTTP/3 SETTINGS parameter identifiers (RFC 9114 §7.2.4.1, RFC 9204 §5,
/// RFC 9220 §3), carried as QUIC variable-length integers in the SETTINGS
/// frame on a control stream.
/// </summary>
/// <remarks>
/// The identifier space is shared across both directions: the settings a peer
/// advertises to the server (recorded in <see cref="Http3PeerSettings"/>) and
/// the settings the server advertises on its own control stream (encoded by
/// <see cref="Http3LocalSettings"/>) draw from the same registry, so both sides
/// reference these constants rather than duplicating the literals.
/// </remarks>
internal static class Http3SettingId
{
    /// <summary>QPACK_MAX_TABLE_CAPACITY (RFC 9204 §5).</summary>
    public const long QPackMaxTableCapacity = 0x01;

    /// <summary>MAX_FIELD_SECTION_SIZE (RFC 9114 §7.2.4.1).</summary>
    public const long MaxFieldSectionSize = 0x06;

    /// <summary>QPACK_BLOCKED_STREAMS (RFC 9204 §5).</summary>
    public const long QPackBlockedStreams = 0x07;

    /// <summary>SETTINGS_ENABLE_CONNECT_PROTOCOL (RFC 9220 §3).</summary>
    public const long EnableConnectProtocol = 0x08;
}
