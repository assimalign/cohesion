namespace Assimalign.Cohesion.Http.Transports.Internal.Http3;

/// <summary>
/// HTTP/3 unidirectional stream type identifiers (RFC 9114 §6.2 / §11.2.4),
/// carried as a QUIC variable-length integer prefix on each unidirectional
/// stream. Bidirectional streams carry no such prefix — they are request
/// streams.
/// </summary>
internal static class Http3StreamType
{
    /// <summary>The control stream (RFC 9114 §6.2.1).</summary>
    public const long Control = 0x00;

    /// <summary>A server push stream (RFC 9114 §6.2.2). A client must not open one.</summary>
    public const long Push = 0x01;

    /// <summary>The QPACK encoder stream (RFC 9204 §4.2).</summary>
    public const long QPackEncoder = 0x02;

    /// <summary>The QPACK decoder stream (RFC 9204 §4.2).</summary>
    public const long QPackDecoder = 0x03;
}
