using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// The connection info of an exchange that arrived over TLS: the endpoints, plus what the handshake
/// negotiated, published as the contracts library's <see cref="ITlsConnectionInfo"/> facet so a reader
/// finds it with a type test on <see cref="IHttpContext.ConnectionInfo"/> (or on an interceptor
/// context's <c>ConnectionInfo</c>).
/// </summary>
/// <remarks>
/// <para>
/// The transport installs no HTTP TLS feature and references no package that declares one: the
/// application-facing <c>context.TlsConnection</c> accessor (<c>Assimalign.Cohesion.Http.Tls</c>) builds
/// its feature from this facet. Publishing the facts on the connection info makes them visible from
/// the first request-parse hook onward, because the transport hands the same instance to the request
/// interceptor context, the exchange, and the response interceptor context.
/// </para>
/// <para>
/// The four values are copied when the instance is built, and the connection that ran the handshake is
/// never referenced: a handler that cast this object back to the live connection could otherwise reach
/// members the transport alone may use (a QUIC connection opens streams). The certificate is the one
/// exception to "copied": it is the connection's own instance, which the connection disposes when it
/// closes, so code that keeps it beyond the exchange copies it first.
/// </para>
/// </remarks>
internal sealed class HttpTlsConnectionInfo : HttpConnectionInfo, ITlsConnectionInfo
{
    private HttpTlsConnectionInfo(EndPoint? localEndPoint, EndPoint? remoteEndPoint, ITlsConnectionInfo tls)
        : base(localEndPoint, remoteEndPoint)
    {
        ApplicationProtocol = tls.ApplicationProtocol;
        TlsProtocol = tls.TlsProtocol;
        CipherSuite = tls.CipherSuite;
        RemoteCertificate = tls.RemoteCertificate;
    }

    /// <inheritdoc />
    public SslApplicationProtocol ApplicationProtocol { get; }

    /// <inheritdoc />
    public SslProtocols TlsProtocol { get; }

    /// <inheritdoc />
    public TlsCipherSuite CipherSuite { get; }

    /// <inheritdoc />
    public X509Certificate2? RemoteCertificate { get; }

    /// <summary>
    /// Creates the connection info for an exchange: one that also carries the handshake facts when the
    /// connection reports them, or a plain <see cref="HttpConnectionInfo"/> when it does not (a cleartext
    /// connection, or a TLS layer that does not implement <see cref="ITlsConnectionInfo"/>).
    /// </summary>
    /// <param name="localEndPoint">The local endpoint of the connection or stream.</param>
    /// <param name="remoteEndPoint">The remote endpoint of the connection or stream.</param>
    /// <param name="tls">The handshake facet of the accepted connection, or <see langword="null"/>.</param>
    /// <returns>The connection info.</returns>
    public static HttpConnectionInfo Create(EndPoint? localEndPoint, EndPoint? remoteEndPoint, ITlsConnectionInfo? tls)
    {
        return tls is null
            ? new HttpConnectionInfo(localEndPoint, remoteEndPoint)
            : new HttpTlsConnectionInfo(localEndPoint, remoteEndPoint, tls);
    }
}
