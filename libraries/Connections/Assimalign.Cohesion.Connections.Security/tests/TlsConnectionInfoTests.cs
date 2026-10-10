using System;
using System.Collections.Generic;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;

namespace Assimalign.Cohesion.Connections.Security.Tests;

/// <summary>
/// Pins what a secured connection reports about its handshake through
/// <see cref="ITlsConnectionInfo"/>, on both the server and the client side of a TLS session run over
/// an in-memory connection pair.
/// </summary>
public class TlsConnectionInfoTests : IClassFixture<TestCertificateFixture>
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(10);

    private readonly TestCertificateFixture _fixture;

    public TlsConnectionInfoTests(TestCertificateFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Security] - ApplicationProtocol: Should report h2 when both sides offer it")]
    public async Task ApplicationProtocol_WhenBothSidesOfferH2_ShouldReportH2()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);

        // Act
        (IConnection client, IConnection server) = await UpgradePairAsync(
            [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11],
            [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11],
            timeout.Token);

        // Assert
        server.ShouldBeAssignableTo<ITlsConnectionInfo>()!.ApplicationProtocol.ShouldBe(SslApplicationProtocol.Http2);
        client.ShouldBeAssignableTo<ITlsConnectionInfo>()!.ApplicationProtocol.ShouldBe(SslApplicationProtocol.Http2);
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Security] - ApplicationProtocol: Should report http/1.1 when the client offers only it")]
    public async Task ApplicationProtocol_WhenClientOffersOnlyHttp11_ShouldReportHttp11()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);

        // Act
        (IConnection _, IConnection server) = await UpgradePairAsync(
            [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11],
            [SslApplicationProtocol.Http11],
            timeout.Token);

        // Assert
        server.ShouldBeAssignableTo<ITlsConnectionInfo>()!.ApplicationProtocol.ShouldBe(SslApplicationProtocol.Http11);
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Security] - ApplicationProtocol: Should report no protocol when the client sends no ALPN extension")]
    public async Task ApplicationProtocol_WhenClientOffersNone_ShouldReportDefault()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);

        // Act
        (IConnection _, IConnection server) = await UpgradePairAsync(
            [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11],
            clientProtocols: null,
            timeout.Token);

        // Assert
        server.ShouldBeAssignableTo<ITlsConnectionInfo>()!.ApplicationProtocol.Protocol.IsEmpty.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Security] - TlsProtocol and CipherSuite: Should report what both peers negotiated")]
    public async Task TlsProtocolAndCipherSuite_OnCompletedHandshake_ShouldMatchOnBothPeers()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);

        // Act
        (IConnection client, IConnection server) = await UpgradePairAsync(null, null, timeout.Token);
        ITlsConnectionInfo serverTls = server.ShouldBeAssignableTo<ITlsConnectionInfo>()!;
        ITlsConnectionInfo clientTls = client.ShouldBeAssignableTo<ITlsConnectionInfo>()!;

        // Assert — a modern platform negotiates TLS 1.2 or 1.3, and both ends agree on the session.
        serverTls.TlsProtocol.ShouldBeOneOf(SslProtocols.Tls12, SslProtocols.Tls13);
        serverTls.TlsProtocol.ShouldBe(clientTls.TlsProtocol);
        serverTls.CipherSuite.ShouldNotBe(default);
        serverTls.CipherSuite.ShouldBe(clientTls.CipherSuite);
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Security] - RemoteCertificate: Should report the server's certificate on the client side")]
    public async Task RemoteCertificate_OnClientSide_ShouldBeServerCertificate()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);

        // Act
        (IConnection client, IConnection _) = await UpgradePairAsync(null, null, timeout.Token);

        // Assert
        client.ShouldBeAssignableTo<ITlsConnectionInfo>()!.RemoteCertificate!.Thumbprint.ShouldBe(_fixture.Certificate.Thumbprint);
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Security] - RemoteCertificate: Should be null on the server when no client certificate was requested")]
    public async Task RemoteCertificate_WithoutClientCertificate_ShouldBeNullOnServer()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);

        // Act
        (IConnection _, IConnection server) = await UpgradePairAsync(null, null, timeout.Token);

        // Assert
        server.ShouldBeAssignableTo<ITlsConnectionInfo>()!.RemoteCertificate.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Security] - RemoteCertificate: Should report the client's certificate on the server and release it with the connection")]
    public async Task RemoteCertificate_WithClientCertificate_ShouldBeOwnedByServerConnection()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);

        // Act
        (IConnection _, IConnection server) = await UpgradePairAsync(
            null,
            null,
            timeout.Token,
            serverOptions => serverOptions.AllowClientCertificate(static (_, _, _) => true),
            clientOptions => UseClientCertificate(clientOptions, _fixture.ClientCertificate));
        X509Certificate2 certificate = server.ShouldBeAssignableTo<ITlsConnectionInfo>()!.RemoteCertificate.ShouldNotBeNull();
        string thumbprint = certificate.Thumbprint;
        await server.DisposeAsync();

        // Assert — the connection owned the certificate and released it with itself.
        thumbprint.ShouldBe(_fixture.ClientCertificate.Thumbprint);
        certificate.Handle.ShouldBe(IntPtr.Zero);
    }

    internal static void UseClientCertificate(TlsClientOptions options, X509Certificate2 certificate)
    {
        options.AuthenticationOptions.ClientCertificates = new X509CertificateCollection { certificate };
        options.AuthenticationOptions.LocalCertificateSelectionCallback = (_, _, _, _, _) => certificate;
    }

    private async Task<(IConnection Client, IConnection Server)> UpgradePairAsync(
        List<SslApplicationProtocol>? serverProtocols,
        List<SslApplicationProtocol>? clientProtocols,
        CancellationToken cancellationToken,
        Action<TlsServerOptions>? configureServer = null,
        Action<TlsClientOptions>? configureClient = null)
    {
        (Connection client, Connection server) = InMemoryConnectionPair.Create();
        TlsServerOptions serverOptions = new()
        {
            AuthenticationOptions =
            {
                ServerCertificate = _fixture.Certificate,
                ApplicationProtocols = serverProtocols
            }
        };
        TlsClientOptions clientOptions = new()
        {
            AuthenticationOptions =
            {
                TargetHost = "localhost",
                ApplicationProtocols = clientProtocols,
                RemoteCertificateValidationCallback = static (_, _, _, _) => true
            }
        };
        configureServer?.Invoke(serverOptions);
        configureClient?.Invoke(clientOptions);

        Task<IConnection> serverUpgrade = server.UpgradeToTlsAsync(serverOptions, cancellationToken).AsTask();
        Task<IConnection> clientUpgrade = client.UpgradeToTlsAsync(clientOptions, cancellationToken).AsTask();

        await Task.WhenAll(serverUpgrade, clientUpgrade);

        return (await clientUpgrade, await serverUpgrade);
    }
}
