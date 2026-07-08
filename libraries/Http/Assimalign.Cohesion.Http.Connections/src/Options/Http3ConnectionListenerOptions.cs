namespace Assimalign.Cohesion.Http.Connections;

/// <summary>
/// Configuration for the HTTP/3 (QUIC) listener registered through
/// <see cref="HttpConnectionListenerOptions.UseHttp3(System.Func{Assimalign.Cohesion.Connections.IMultiplexedConnectionListener}, System.Action{Http3ConnectionListenerOptions})"/>.
/// HTTP/3-specific tunables live here — rather than on the shared
/// <see cref="HttpConnectionListenerOptions"/> — so each protocol version owns
/// its own configuration surface.
/// </summary>
public sealed class Http3ConnectionListenerOptions
{
    /// <summary>
    /// Gets the QPACK field-compression configuration (RFC 9204). The default is
    /// the static-only profile (the dynamic table is disabled).
    /// </summary>
    public Http3QPackOptions QPack { get; } = new();

    /// <summary>
    /// Gets the shared request-shaping limits applied to every connection this registration
    /// accepts. The bounds carry the same conservative defaults as the other protocol versions;
    /// mutate the returned instance to tune them.
    /// </summary>
    public Http3Limits Limits { get; } = new Http3Limits();

    /// <summary>
    /// The HTTP/3 limits: the shared <see cref="HttpConnectionListenerLimits"/> with no
    /// version-specific additions.
    /// </summary>
    /// <remarks>
    /// HTTP/3's frame-rate and concurrency bounds are governed by the QUIC transport (stream
    /// limits, connection flow control), so this type adds no caps of its own. Of the inherited
    /// shared limits, <see cref="HttpConnectionListenerLimits.MaxRequestBodySize"/> seeds each
    /// request's parse context — the value request-parse interceptors observe and adjust and the
    /// <c>Assimalign.Cohesion.Http.RequestLimits</c> feature exposes — while body buffering is
    /// bounded by QUIC flow control; the hard wire-level cap and the connection timeouts are
    /// tracked follow-up work, matching the HTTP/2 posture.
    /// </remarks>
    public sealed class Http3Limits : HttpConnectionListenerLimits
    {
    }
}
