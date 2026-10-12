using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;

namespace Assimalign.Cohesion.Connections.Security.Tests;

public class TlsConnectionTests : IClassFixture<TestCertificateFixture>
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(5);

    private readonly TestCertificateFixture _fixture;

    public TlsConnectionTests(TestCertificateFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task UpgradeToTlsAsync_WithClientAndServerPair_ShouldExchangeBytesInBothDirections()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        (Connection client, Connection server) = InMemoryConnectionPair.Create();

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
        using CancellationTokenSource timeout = new(_testTimeout);
        (Connection client, Connection server) = InMemoryConnectionPair.Create();

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
        using CancellationTokenSource timeout = new(_testTimeout);
        (Connection client, Connection server) = InMemoryConnectionPair.Create();

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
        using CancellationTokenSource timeout = new(_testTimeout);
        (Connection client, Connection server) = InMemoryConnectionPair.Create();
        (IConnection securedClient, IConnection _) = await UpgradePairAsync(client, server, timeout.Token);

        // Act
        await securedClient.DisposeAsync();

        // Assert
        client.State.ShouldBe(ConnectionState.Closed);
    }

    [Theory(DisplayName = "Cohesion Test [Connections.Security] - Features: TLS hides directional abort when plaintext survives an inner abort")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Features_WithBufferedPlaintext_ShouldHideInnerDirectionalAbort(bool readAtServer)
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        (MultiplexedConnection client, MultiplexedConnection server) = InMemoryMultiplexedConnectionPair.Create();
        await using MultiplexedConnection clientLifetime = client;
        await using MultiplexedConnection serverLifetime = server;
        await using Connection clientStream = await client.OpenStreamAsync(cancellationToken: timeout.Token);
        await using Connection serverStream = await server.AcceptStreamAsync(timeout.Token);
        (IConnection securedClient, IConnection securedServer) = await UpgradePairAsync(clientStream, serverStream, timeout.Token);
        await using IConnection securedClientLifetime = securedClient;
        await using IConnection securedServerLifetime = securedServer;
        IConnection sender = readAtServer ? securedClient : securedServer;
        IConnection receiver = readAtServer ? securedServer : securedClient;
        IConnection innerReceiver = readAtServer ? serverStream : clientStream;
        IMultiplexedStreamAbort innerAbort = innerReceiver.DirectionalAbort.ShouldNotBeNull();
        byte[] payload = [1, 2, 3];
        await sender.Output.WriteAsync(payload, timeout.Token);
        ReadResult read = await receiver.Input.ReadAtLeastAsync(payload.Length, timeout.Token);
        read.Buffer.ToArray().ShouldBe(payload);
        receiver.Input.AdvanceTo(read.Buffer.Start, read.Buffer.Start);

        // Act: aborting the transport cannot invalidate plaintext retained by TLS's reader.
        innerAbort.AbortRead(42);
        bool hasBufferedPlaintext = receiver.Input.TryRead(out ReadResult buffered);

        // Assert: this inner feature cannot honor its buffered-read guarantee at the TLS layer.
        hasBufferedPlaintext.ShouldBeTrue();
        buffered.Buffer.ToArray().ShouldBe(payload);
        receiver.Input.AdvanceTo(buffered.Buffer.End);
        receiver.DirectionalAbort.ShouldBeNull();
        receiver.Features.Get<IMultiplexedStreamAbort>().ShouldBeNull();
        receiver.Features.Get(IMultiplexedStreamAbort.FeatureName).ShouldBeNull();
        receiver.Features.ShouldNotContain(feature => feature.Name == IMultiplexedStreamAbort.FeatureName);
        innerReceiver.DirectionalAbort.ShouldBeSameAs(innerAbort);
        receiver.Tls.ShouldNotBeNull();
    }

    private async Task<(IConnection Client, IConnection Server)> UpgradePairAsync(
        Connection client,
        Connection server,
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
