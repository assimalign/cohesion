using System;
using System.Net.Security;

namespace Assimalign.Cohesion.Connections.Security;

/// <summary>
/// Options that control a client-side TLS connection upgrade.
/// </summary>
public sealed class TlsClientOptions
{
    /// <summary>
    /// Gets or sets the underlying TLS client authentication options (target host, enabled
    /// protocols, ALPN application protocols, client certificates, and so on).
    /// </summary>
    public SslClientAuthenticationOptions AuthenticationOptions { get; set; } = new();

    /// <summary>
    /// Gets or sets the maximum time allowed for the TLS handshake to complete.
    /// </summary>
    /// <remarks>
    /// Defaults to 10 seconds. A non-positive value disables the timeout.
    /// </remarks>
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(10);
}
