using System;
using System.IO.Pipelines;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

/// <summary>
/// A <see cref="Connection"/> double for a connection a TLS layer secured: it reports
/// <see cref="ConnectionSecurity.Tls"/> and the handshake's results through
/// <see cref="ITlsConnectionInfo"/>, and delegates its pipes and lifetime to a plaintext
/// <see cref="TestConnection"/> that carries the preloaded request bytes and captures the response.
/// </summary>
internal sealed class TestTlsConnection : Connection, ITlsConnectionInfo
{
    public TestTlsConnection(
        byte[] input,
        SslApplicationProtocol applicationProtocol,
        X509Certificate2? remoteCertificate = null,
        SslProtocols tlsProtocol = SslProtocols.Tls13,
        TlsCipherSuite cipherSuite = TlsCipherSuite.TLS_AES_256_GCM_SHA384)
    {
        Inner = new TestConnection(input, capabilities: TlsCapabilities);
        ApplicationProtocol = applicationProtocol;
        RemoteCertificate = remoteCertificate;
        TlsProtocol = tlsProtocol;
        CipherSuite = cipherSuite;
    }

    /// <summary>
    /// The capabilities a TLS-layered stream listener reports: the in-memory stream defaults with
    /// <see cref="ConnectionCapabilities.Security"/> set to <see cref="ConnectionSecurity.Tls"/>.
    /// </summary>
    public static ConnectionCapabilities TlsCapabilities => TestConnection.DefaultCapabilities with { Security = ConnectionSecurity.Tls };

    /// <summary>
    /// The plaintext connection whose pipes this double exposes; read the response from it.
    /// </summary>
    public TestConnection Inner { get; }

    public SslApplicationProtocol ApplicationProtocol { get; }

    public SslProtocols TlsProtocol { get; }

    public TlsCipherSuite CipherSuite { get; }

    public X509Certificate2? RemoteCertificate { get; }

    public override ConnectionId Id => Inner.Id;

    public override EndPoint? LocalEndPoint => Inner.LocalEndPoint;

    public override EndPoint? RemoteEndPoint => Inner.RemoteEndPoint;

    public override PipeReader Input => Inner.Input;

    public override PipeWriter Output => Inner.Output;

    public override ConnectionCapabilities Capabilities => TlsCapabilities;

    public override ConnectionState State => Inner.State;

    public override CancellationToken ConnectionClosed => Inner.ConnectionClosed;

    public override void Abort(Exception? reason = null) => Inner.Abort(reason);

    public override ValueTask DisposeAsync() => Inner.DisposeAsync();
}
