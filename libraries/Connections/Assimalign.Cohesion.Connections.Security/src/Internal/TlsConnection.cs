using System;
using System.IO.Pipelines;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Connections.Security.Internal;

/// <summary>
/// An <see cref="IConnection"/> decorator that secures an inner connection with a TLS session.
/// Identity, endpoints, state, and lifetime are delegated to the inner connection; the duplex pipe
/// is replaced with a TLS-encrypted one and <see cref="Capabilities"/> reports
/// <see cref="ConnectionSecurity.Tls"/>. What the handshake negotiated is reported through
/// <see cref="ITlsConnectionInfo"/>, captured once the handshake has completed.
/// </summary>
internal sealed class TlsConnection : Connection, ITlsConnectionInfo
{
    private readonly IConnection _inner;
    private readonly SslStream _ssl;
    private readonly PipeReader _input;
    private readonly PipeWriter _output;
    private readonly ConnectionCapabilities _capabilities;
    private readonly SslApplicationProtocol _applicationProtocol;
    private readonly SslProtocols _tlsProtocol;
    private readonly TlsCipherSuite _cipherSuite;
    private readonly X509Certificate2? _remoteCertificate;

    private TlsConnection(IConnection inner, SslStream ssl)
    {
        _inner = inner;
        _ssl = ssl;
        _input = PipeReader.Create(ssl);
        _output = PipeWriter.Create(ssl);
        _capabilities = inner.Capabilities with { Security = ConnectionSecurity.Tls };
        _applicationProtocol = ssl.NegotiatedApplicationProtocol;
        _tlsProtocol = ssl.SslProtocol;
        _cipherSuite = ssl.NegotiatedCipherSuite;
        _remoteCertificate = TakeRemoteCertificate(ssl);
    }

    public override ConnectionId Id => _inner.Id;

    public override EndPoint? LocalEndPoint => _inner.LocalEndPoint;

    public override EndPoint? RemoteEndPoint => _inner.RemoteEndPoint;

    public override PipeReader Input => _input;

    public override PipeWriter Output => _output;

    public override ConnectionDirection Direction => _inner.Direction;

    public override ConnectionCapabilities Capabilities => _capabilities;

    public override ConnectionState State => _inner.State;

    public override CancellationToken ConnectionClosed => _inner.ConnectionClosed;

    public SslApplicationProtocol ApplicationProtocol => _applicationProtocol;

    public SslProtocols TlsProtocol => _tlsProtocol;

    public TlsCipherSuite CipherSuite => _cipherSuite;

    public X509Certificate2? RemoteCertificate => _remoteCertificate;

    public override void Abort(Exception? reason = null) => _inner.Abort(reason);

    public override async ValueTask DisposeAsync()
    {
        await _ssl.DisposeAsync().ConfigureAwait(false);
        await _inner.DisposeAsync().ConfigureAwait(false);

        // The connection took ownership of the peer's certificate when it read it (see
        // TakeRemoteCertificate), so it releases the certificate with itself.
        _remoteCertificate?.Dispose();
    }

    /// <summary>
    /// Reads the certificate the peer presented. Reading <see cref="SslStream.RemoteCertificate"/>
    /// hands its ownership to the caller (an <see cref="SslStream"/> no longer disposes a certificate it
    /// has exposed), so this connection disposes it. The platform stacks surface an
    /// <see cref="X509Certificate2"/>; any other instance is copied and released.
    /// </summary>
    private static X509Certificate2? TakeRemoteCertificate(SslStream ssl)
    {
        X509Certificate? certificate = ssl.RemoteCertificate;
        if (certificate is null or X509Certificate2)
        {
            return (X509Certificate2?)certificate;
        }

        try
        {
            return X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        }
        finally
        {
            certificate.Dispose();
        }
    }

    internal static async ValueTask<IConnection> AuthenticateAsServerAsync(IConnection inner, TlsServerOptions options, CancellationToken cancellationToken)
    {
        SslStream ssl = new(new DuplexPipeStream(inner), leaveInnerStreamOpen: false);
        bool authenticated = false;
        try
        {
            using CancellationTokenSource handshake = CreateHandshakeTokenSource(options.HandshakeTimeout, cancellationToken);
            await ssl.AuthenticateAsServerAsync(options.AuthenticationOptions, handshake.Token).ConfigureAwait(false);
            authenticated = true;
        }
        finally
        {
            if (!authenticated)
            {
                await ssl.DisposeAsync().ConfigureAwait(false);
            }
        }

        return new TlsConnection(inner, ssl);
    }

    internal static async ValueTask<IConnection> AuthenticateAsClientAsync(IConnection inner, TlsClientOptions options, CancellationToken cancellationToken)
    {
        SslStream ssl = new(new DuplexPipeStream(inner), leaveInnerStreamOpen: false);
        bool authenticated = false;
        try
        {
            using CancellationTokenSource handshake = CreateHandshakeTokenSource(options.HandshakeTimeout, cancellationToken);
            await ssl.AuthenticateAsClientAsync(options.AuthenticationOptions, handshake.Token).ConfigureAwait(false);
            authenticated = true;
        }
        finally
        {
            if (!authenticated)
            {
                await ssl.DisposeAsync().ConfigureAwait(false);
            }
        }

        return new TlsConnection(inner, ssl);
    }

    private static CancellationTokenSource CreateHandshakeTokenSource(TimeSpan timeout, CancellationToken cancellationToken)
    {
        CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout > TimeSpan.Zero)
        {
            source.CancelAfter(timeout);
        }

        return source;
    }
}
