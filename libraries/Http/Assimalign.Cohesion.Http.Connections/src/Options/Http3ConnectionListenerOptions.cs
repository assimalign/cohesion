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
}
