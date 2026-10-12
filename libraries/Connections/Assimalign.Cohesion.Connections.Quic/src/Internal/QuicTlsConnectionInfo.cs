using System.Net.Security;
using System.Runtime.Versioning;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Assimalign.Cohesion.Connections.Quic.Internal;

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
internal sealed class QuicTlsConnectionInfo : ITlsConnectionInfo
{
    private readonly QuicMultiplexedConnection _connection;

    public QuicTlsConnectionInfo(QuicMultiplexedConnection connection)
    {
        _connection = connection;
    }

    public string Name => ITlsConnectionInfo.FeatureName;
    public SslApplicationProtocol ApplicationProtocol => _connection.ApplicationProtocol;
    public SslProtocols TlsProtocol => _connection.TlsProtocol;
    public TlsCipherSuite CipherSuite => _connection.CipherSuite;
    public X509Certificate2? RemoteCertificate => _connection.RemoteCertificate;
}
