using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Assimalign.Cohesion.Http.Tls.Tests.TestObjects;

/// <summary>
/// An application-supplied <see cref="IHttpTlsConnectionFeature"/>, as a middleware installs one to
/// override what the connection reports.
/// </summary>
internal sealed class TestTlsConnectionFeature : IHttpTlsConnectionFeature
{
    public string Name => "Assimalign.Cohesion.Http.TlsConnection";

    public X509Certificate2? ClientCertificate => null;

    public SslProtocols Protocol => SslProtocols.Tls12;

    public TlsCipherSuite CipherSuite => TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384;

    public SslApplicationProtocol ApplicationProtocol => SslApplicationProtocol.Http11;
}
