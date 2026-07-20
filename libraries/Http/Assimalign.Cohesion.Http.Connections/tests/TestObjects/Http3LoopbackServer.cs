using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.Quic;

namespace Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

/// <summary>
/// A loopback HTTP/3 server harness for the transport's real-QUIC round-trip tests. It binds a
/// <see cref="QuicConnectionListener"/> on a free loopback UDP port, drives every accepted
/// connection through the HTTP/3 engine, and applies a test-supplied handler to each request
/// before sending the response. Disposal stops the accept loop and releases the listener and
/// certificate.
/// </summary>
/// <remarks>
/// Mirrors the <c>Assimalign.Cohesion.Http.Connections.Examples.Http3</c> server wiring so the
/// tests exercise the same composition a real deployment uses. QUIC is Windows/Linux/macOS only,
/// so the harness (and its tests) are platform-annotated and gated at runtime on
/// <see cref="System.Net.Quic.QuicListener.IsSupported"/>.
/// </remarks>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
internal sealed class Http3LoopbackServer : IAsyncDisposable
{
    private readonly HttpConnectionListener _listener;
    private readonly X509Certificate2 _certificate;
    private readonly CancellationTokenSource _shutdown;
    private readonly Task _serveLoop;

    private Http3LoopbackServer(
        HttpConnectionListener listener,
        X509Certificate2 certificate,
        int port,
        Func<IHttpContext, Task> handler)
    {
        _listener = listener;
        _certificate = certificate;
        _shutdown = new CancellationTokenSource();
        BaseUri = new Uri($"https://127.0.0.1:{port}/");
        _serveLoop = Task.Run(() => ServeAsync(handler, _shutdown.Token));
    }

    /// <summary>The base URI a real HTTP/3 client dials to reach this server.</summary>
    public Uri BaseUri { get; }

    /// <summary>
    /// Binds a loopback HTTP/3 listener and starts serving. Each surfaced request is passed to
    /// <paramref name="handler"/> (which sets the response), then the response is written and the
    /// exchange disposed.
    /// </summary>
    /// <param name="handler">The per-request response handler.</param>
    /// <param name="cancellationToken">A token that cancels the initial QUIC bind.</param>
    /// <returns>The started server harness.</returns>
    public static async Task<Http3LoopbackServer> StartAsync(Func<IHttpContext, Task> handler, CancellationToken cancellationToken)
    {
        int port = AllocateUdpPort();
        X509Certificate2 certificate = CreateCertificate();

        QuicConnectionListener quicListener = await QuicConnectionListener.CreateAsync(transport =>
        {
            transport.EndPoint = new IPEndPoint(IPAddress.Loopback, port);
            transport.ServerAuthenticationOptions.ServerCertificate = certificate;
            transport.ServerAuthenticationOptions.ApplicationProtocols = new List<SslApplicationProtocol> { SslApplicationProtocol.Http3 };
        }, cancellationToken).ConfigureAwait(false);

        HttpConnectionListener listener = HttpConnectionListener.Create(options => options.UseHttp3(quicListener));

        return new Http3LoopbackServer(listener, certificate, port, handler);
    }

    private async Task ServeAsync(Func<IHttpContext, Task> handler, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                IHttpConnection connection = await _listener.AcceptOrListenAsync(cancellationToken).ConfigureAwait(false);

                // Serve each connection on its own task so a second client (or reconnect) is not
                // blocked behind the first; the client under test reuses one connection for its
                // sequential requests, which the receive loop yields in order.
                _ = ServeConnectionAsync(connection, handler, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown requested — stop accepting.
        }
        catch (ObjectDisposedException)
        {
            // The listener was disposed out from under the accept — teardown raced the loop.
        }
    }

    private static async Task ServeConnectionAsync(IHttpConnection connection, Func<IHttpContext, Task> handler, CancellationToken cancellationToken)
    {
        try
        {
            await using (connection.ConfigureAwait(false))
            {
                IHttpConnectionContext context = await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

                await foreach (IHttpContext exchange in context.ReceiveAsync(cancellationToken).ConfigureAwait(false))
                {
                    await handler(exchange).ConfigureAwait(false);
                    await context.SendAsync(exchange, cancellationToken).ConfigureAwait(false);
                    await exchange.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        catch (Exception)
        {
            // Test harness: any connection-scoped teardown (client abort, QUIC reset, idle timeout,
            // or shutdown cancellation) is expected and must not fault the background serve loop.
            // The assertions live on the client side of each test, not here.
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();

        try
        {
            await _serveLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        await _listener.DisposeAsync().ConfigureAwait(false);
        _certificate.Dispose();
        _shutdown.Dispose();
    }

    private static int AllocateUdpPort()
    {
        using Socket probe = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    private static X509Certificate2 CreateCertificate()
    {
        using RSA rsa = RSA.Create(2048);
        CertificateRequest request = new("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        SubjectAlternativeNameBuilder subjectAlternativeNames = new();
        subjectAlternativeNames.AddDnsName("localhost");
        subjectAlternativeNames.AddIpAddress(IPAddress.Loopback);

        request.CertificateExtensions.Add(subjectAlternativeNames.Build());
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, // id-kp-serverAuth
            critical: false));

        using X509Certificate2 ephemeral = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(10));

        // Round-trip through PKCS#12 so the private key is persisted in a form the platform TLS
        // stack (Schannel / MsQuic) accepts; a certificate with an ephemeral key is rejected on Windows.
        return X509CertificateLoader.LoadPkcs12(
            ephemeral.Export(X509ContentType.Pfx),
            password: null,
            X509KeyStorageFlags.Exportable);
    }
}
