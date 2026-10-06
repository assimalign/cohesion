using System;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

/// <summary>
/// A <see cref="MultiplexedConnection"/> double for a QUIC connection: it reports its TLS 1.3 handshake
/// through <see cref="ITlsConnectionInfo"/> (QUIC carries TLS itself, RFC 9001) and delegates its streams
/// and lifetime to a <see cref="TestMultiplexedConnection"/>.
/// </summary>
internal sealed class TestTlsMultiplexedConnection : MultiplexedConnection, ITlsConnectionInfo
{
    private readonly TestMultiplexedConnection _inner;

    public TestTlsMultiplexedConnection(X509Certificate2? remoteCertificate, params Connection[] streams)
    {
        _inner = new TestMultiplexedConnection(streams);
        RemoteCertificate = remoteCertificate;
    }

    public SslApplicationProtocol ApplicationProtocol => SslApplicationProtocol.Http3;

    public SslProtocols TlsProtocol => SslProtocols.Tls13;

    public TlsCipherSuite CipherSuite => TlsCipherSuite.TLS_AES_128_GCM_SHA256;

    public X509Certificate2? RemoteCertificate { get; }

    public override ConnectionId Id => _inner.Id;

    public override EndPoint? LocalEndPoint => _inner.LocalEndPoint;

    public override EndPoint? RemoteEndPoint => _inner.RemoteEndPoint;

    public override ConnectionCapabilities Capabilities => _inner.Capabilities;

    public override ConnectionState State => _inner.State;

    public override CancellationToken ConnectionClosed => _inner.ConnectionClosed;

    public override void Abort(Exception? reason = null) => _inner.Abort(reason);

    public override ValueTask DisposeAsync() => _inner.DisposeAsync();

    public override ValueTask<Connection> AcceptStreamAsync(CancellationToken cancellationToken = default)
        => _inner.AcceptStreamAsync(cancellationToken);

    public override ValueTask<Connection> OpenStreamAsync(ConnectionDirection direction = ConnectionDirection.Bidirectional, CancellationToken cancellationToken = default)
        => _inner.OpenStreamAsync(direction, cancellationToken);
}
