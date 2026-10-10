using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Tls.Tests.TestObjects;

/// <summary>
/// Connection info that carries the <see cref="ITlsConnectionInfo"/> facet, the shape the server
/// transport publishes for an exchange that arrived over TLS.
/// </summary>
internal sealed class TestTlsConnectionInfo : HttpConnectionInfo, ITlsConnectionInfo
{
    public TestTlsConnectionInfo(
        SslApplicationProtocol applicationProtocol,
        X509Certificate2? remoteCertificate = null,
        SslProtocols tlsProtocol = SslProtocols.Tls13,
        TlsCipherSuite cipherSuite = TlsCipherSuite.TLS_AES_128_GCM_SHA256)
    {
        ApplicationProtocol = applicationProtocol;
        RemoteCertificate = remoteCertificate;
        TlsProtocol = tlsProtocol;
        CipherSuite = cipherSuite;
    }

    public SslApplicationProtocol ApplicationProtocol { get; }

    public SslProtocols TlsProtocol { get; }

    public TlsCipherSuite CipherSuite { get; }

    public X509Certificate2? RemoteCertificate { get; }
}
