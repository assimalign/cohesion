using System.Net.Security;
using System.Security.Authentication;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;

/// <summary>
/// The TLS session values a handler read from <c>context.TlsConnection</c>, copied so they outlive the
/// exchange.
/// </summary>
/// <param name="Present">Whether the exchange carried a TLS connection feature.</param>
/// <param name="ClientCertificateThumbprint">The client certificate's thumbprint, or <see langword="null"/>.</param>
/// <param name="Protocol">The TLS protocol version.</param>
/// <param name="CipherSuite">The cipher suite.</param>
/// <param name="ApplicationProtocol">The application protocol ALPN selected.</param>
internal sealed record ObservedSession(
    bool Present,
    string? ClientCertificateThumbprint,
    SslProtocols Protocol,
    TlsCipherSuite CipherSuite,
    SslApplicationProtocol ApplicationProtocol)
{
    /// <summary>
    /// Copies the values of <paramref name="feature"/>, or records an absent feature.
    /// </summary>
    /// <param name="feature">The feature the exchange carried, if any.</param>
    /// <returns>The copied session.</returns>
    public static ObservedSession From(IHttpTlsConnectionFeature? feature)
    {
        return feature is null
            ? new ObservedSession(false, null, SslProtocols.None, default, default)
            : new ObservedSession(
                true,
                feature.ClientCertificate?.Thumbprint,
                feature.Protocol,
                feature.CipherSuite,
                feature.ApplicationProtocol);
    }
}
