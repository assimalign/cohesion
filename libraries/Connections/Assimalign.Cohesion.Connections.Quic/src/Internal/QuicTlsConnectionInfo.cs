using System.Net.Security;
using System.Runtime.Versioning;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Assimalign.Cohesion.Connections.Quic.Internal;

[global::System.Diagnostics.DebuggerDisplay("QuicTlsConnectionInfo {Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
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

    private sealed class DebugView
    {
        private readonly QuicTlsConnectionInfo _target;

        public DebugView(QuicTlsConnectionInfo value)
        {
            _target = value;
        }

        public string Name => _target.Name;
        public SslApplicationProtocol ApplicationProtocol => _target.ApplicationProtocol;
        public SslProtocols TlsProtocol => _target.TlsProtocol;
        public TlsCipherSuite CipherSuite => _target.CipherSuite;
        public X509Certificate2? RemoteCertificate => _target.RemoteCertificate;
    }
}
