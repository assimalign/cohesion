using System;
using System.Collections.Generic;
using System.Net.Security;
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

    private async Task<(IConnection Client, IConnection Server)> UpgradePairAsync(
        List<SslApplicationProtocol> serverProtocols,
        List<SslApplicationProtocol>? clientProtocols,
        CancellationToken cancellationToken)
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

        Task<IConnection> serverUpgrade = server.UpgradeToTlsAsync(serverOptions, cancellationToken).AsTask();
        Task<IConnection> clientUpgrade = client.UpgradeToTlsAsync(clientOptions, cancellationToken).AsTask();

        await Task.WhenAll(serverUpgrade, clientUpgrade);

        return (await clientUpgrade, await serverUpgrade);
    }
}
