using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// The transport's <see cref="IHttpTlsConnectionFeature"/>: what the TLS handshake of one connection
/// negotiated, copied from the connection's <see cref="ITlsConnectionInfo"/> when the connection
/// context opens and attached to every exchange the connection carries.
/// </summary>
/// <remarks>
/// One instance per connection, shared by its exchanges; it is immutable, so concurrent HTTP/2 and
/// HTTP/3 exchanges read it freely. It deliberately implements neither <see cref="System.IDisposable"/>
/// nor <see cref="System.IAsyncDisposable"/>: an exchange's disposal walk disposes the disposable
/// features it carries, and the certificate belongs to the connection, which outlives each exchange.
/// </remarks>
internal sealed class HttpTlsConnectionFeature : IHttpTlsConnectionFeature
{
    /// <summary>
    /// The name this feature registers under in the exchange's feature collection.
    /// </summary>
    public const string FeatureName = "Assimalign.Cohesion.Http.TlsConnection";

    private HttpTlsConnectionFeature(ITlsConnectionInfo tls)
    {
        ClientCertificate = tls.RemoteCertificate;
        Protocol = tls.TlsProtocol;
        CipherSuite = tls.CipherSuite;
        ApplicationProtocol = tls.ApplicationProtocol;
    }

    /// <inheritdoc />
    public string Name => FeatureName;

    /// <inheritdoc />
    public X509Certificate2? ClientCertificate { get; }

    /// <inheritdoc />
    public SslProtocols Protocol { get; }

    /// <inheritdoc />
    public TlsCipherSuite CipherSuite { get; }

    /// <inheritdoc />
    public SslApplicationProtocol ApplicationProtocol { get; }

    /// <summary>
    /// Creates the feature for a connection that reports its TLS handshake, or returns
    /// <see langword="null"/> for one that does not (a cleartext connection, or a TLS layer that does
    /// not implement <see cref="ITlsConnectionInfo"/>).
    /// </summary>
    /// <param name="connection">The accepted transport connection (a stream connection or a QUIC connection).</param>
    /// <returns>The feature, or <see langword="null"/>.</returns>
    public static HttpTlsConnectionFeature? From(object connection)
    {
        return connection is ITlsConnectionInfo tls ? new HttpTlsConnectionFeature(tls) : null;
    }
}
