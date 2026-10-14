using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Assimalign.Cohesion.Connections.Tls.Tests.TestObjects;

internal sealed class TestTlsFeature : ITlsConnectionInfo
{
    public string Name => ITlsConnectionInfo.FeatureName;

    public SslApplicationProtocol ApplicationProtocol => SslApplicationProtocol.Http2;

    public SslProtocols TlsProtocol => SslProtocols.Tls13;

    public TlsCipherSuite CipherSuite => TlsCipherSuite.TLS_AES_128_GCM_SHA256;

    public X509Certificate2? RemoteCertificate => null;
}
