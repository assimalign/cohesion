namespace Assimalign.Cohesion.Web.Testing;

/// <summary>
/// The HTTP protocol a <see cref="WebApplicationTestFactory"/> serves over the in-memory
/// transport.
/// </summary>
/// <remarks>
/// HTTP/3 is deliberately absent: it is QUIC-bound (transport security and stream lifecycle
/// are inherent to the protocol), so an HTTP/3 test surface stays out of scope until
/// QUIC-over-memory is meaningful — see the project's <c>docs/DESIGN.md</c> and the HTTP/3
/// registration surface tracked under issue #767.
/// </remarks>
public enum WebApplicationTestProtocol
{
    /// <summary>
    /// HTTP/1.1 over the in-memory duplex pair. The default.
    /// </summary>
    Http1 = 0,

    /// <summary>
    /// Prior-knowledge HTTP/2 (h2c) over the in-memory duplex pair: the client speaks
    /// HTTP/2 from the first byte with no TLS/ALPN negotiation and no Upgrade dance,
    /// multiplexing request streams over the single in-memory connection.
    /// </summary>
    Http2 = 1,
}
