using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Connections.Security.Tests;

public class TlsConnectionTests : IClassFixture<TestCertificateFixture>
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    private readonly TestCertificateFixture _fixture;

    public TlsConnectionTests(TestCertificateFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task UpgradeToTlsAsync_WithClientAndServerPair_ShouldExchangeBytesInBothDirections()
    {
        // Arrange
        using CancellationTokenSource timeout = new(TestTimeout);
        (TestPipeConnection client, TestPipeConnection server) = InMemoryConnectionPair.Create();

        // Act
        (IConnection securedClient, IConnection securedServer) = await UpgradePairAsync(client, server, timeout.Token);

        // Assert: bytes written by the client arrive decrypted at the server.
        byte[] clientPayload = Encoding.UTF8.GetBytes("hello from client");
        await securedClient.Output.WriteAsync(clientPayload, timeout.Token);
        byte[] serverReceived = await securedServer.Input.ReadExactlyAsync(clientPayload.Length, timeout.Token);
        serverReceived.ShouldBe(clientPayload);

        // And bytes written by the server arrive decrypted at the client.
        byte[] serverPayload = Encoding.UTF8.GetBytes("hello from server");
        await securedServer.Output.WriteAsync(serverPayload, timeout.Token);
        byte[] clientReceived = await securedClient.Input.ReadExactlyAsync(serverPayload.Length, timeout.Token);
        clientReceived.ShouldBe(serverPayload);
    }

    [Fact]
    public async Task UpgradeToTlsAsync_OnSecuredConnection_ShouldReportTlsSecurityAndPreserveOtherCapabilities()
    {
        // Arrange
        using CancellationTokenSource timeout = new(TestTimeout);
        (TestPipeConnection client, TestPipeConnection server) = InMemoryConnectionPair.Create();

        // Act
        (IConnection securedClient, IConnection securedServer) = await UpgradePairAsync(client, server, timeout.Token);

        // Assert
        securedClient.Capabilities.ShouldBe(client.Capabilities with { Security = ConnectionSecurity.Tls });
        securedServer.Capabilities.ShouldBe(server.Capabilities with { Security = ConnectionSecurity.Tls });
    }

    [Fact]
    public async Task UpgradeToTlsAsync_OnSecuredConnection_ShouldDelegateIdentityToInnerConnection()
    {
        // Arrange
        using CancellationTokenSource timeout = new(TestTimeout);
        (TestPipeConnection client, TestPipeConnection server) = InMemoryConnectionPair.Create();

        // Act
        (IConnection securedClient, IConnection _) = await UpgradePairAsync(client, server, timeout.Token);

        // Assert
        securedClient.Id.ShouldBe(client.Id);
        securedClient.LocalEndPoint.ShouldBeSameAs(client.LocalEndPoint);
        securedClient.RemoteEndPoint.ShouldBeSameAs(client.RemoteEndPoint);
        securedClient.Direction.ShouldBe(client.Direction);
    }

    [Fact]
    public async Task DisposeAsync_OnSecuredConnection_ShouldDisposeInnerConnection()
    {
        // Arrange
        using CancellationTokenSource timeout = new(TestTimeout);
        (TestPipeConnection client, TestPipeConnection server) = InMemoryConnectionPair.Create();
        (IConnection securedClient, IConnection _) = await UpgradePairAsync(client, server, timeout.Token);

        // Act
        await securedClient.DisposeAsync();

        // Assert
        client.IsDisposed.ShouldBeTrue();
    }

    private async Task<(IConnection Client, IConnection Server)> UpgradePairAsync(
        TestPipeConnection client,
        TestPipeConnection server,
        CancellationToken cancellationToken)
    {
        TlsServerOptions serverOptions = new()
        {
            AuthenticationOptions =
            {
                ServerCertificate = _fixture.Certificate
            }
        };
        TlsClientOptions clientOptions = new()
        {
            AuthenticationOptions =
            {
                TargetHost = "localhost",
                RemoteCertificateValidationCallback = static (_, _, _, _) => true
            }
        };

        Task<IConnection> serverUpgrade = server.UpgradeToTlsAsync(serverOptions, cancellationToken).AsTask();
        Task<IConnection> clientUpgrade = client.UpgradeToTlsAsync(clientOptions, cancellationToken).AsTask();

        await Task.WhenAll(serverUpgrade, clientUpgrade);

        return (await clientUpgrade, await serverUpgrade);
    }
}
