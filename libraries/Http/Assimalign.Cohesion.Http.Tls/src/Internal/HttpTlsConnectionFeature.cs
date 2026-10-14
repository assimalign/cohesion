using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Internal;

/// <summary>
/// The <see cref="IHttpTlsConnectionFeature"/> that <c>context.TlsConnection</c> builds: what the TLS
/// handshake of the exchange's connection negotiated, copied from the <see cref="ITlsConnectionInfo"/>
/// facet of the exchange's connection info on first read and cached in the exchange's features.
/// </summary>
/// <remarks>
/// It is immutable, so concurrent readers of one exchange are safe. It deliberately implements neither
/// <see cref="System.IDisposable"/> nor <see cref="System.IAsyncDisposable"/>: an exchange's disposal
/// walk disposes the disposable features it carries, and the certificate belongs to the connection,
/// which outlives each exchange.
/// </remarks>
internal sealed class HttpTlsConnectionFeature : IHttpTlsConnectionFeature
{
    /// <summary>
    /// The name this feature registers under in the exchange's feature collection.
    /// </summary>
    public const string FeatureName = "Assimalign.Cohesion.Http.TlsConnection";

    /// <summary>
    /// Copies the handshake facts of <paramref name="tls"/>.
    /// </summary>
    /// <param name="tls">The handshake facet of the exchange's connection info.</param>
    public HttpTlsConnectionFeature(ITlsConnectionInfo tls)
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
}
