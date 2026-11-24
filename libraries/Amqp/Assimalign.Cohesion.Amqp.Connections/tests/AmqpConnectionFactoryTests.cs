using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Amqp;
using Assimalign.Cohesion.Amqp.Connections.Internal;
using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

public class AmqpConnectionFactoryTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(10);

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ConnectAsync: Should reject a null endpoint")]
    public async Task ConnectAsync_WithNullEndPoint_ShouldThrowArgumentNullException()
    {
        // Arrange
        AmqpConnectionFactory factory = AmqpConnectionFactory.Create(new TestConnectionFactory());

        // Act and Assert
        await Should.ThrowAsync<ArgumentNullException>(async () => await factory.ConnectAsync(null!));
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Properties: Should represent the carrier factory's capabilities")]
    public void Properties_OnFactory_ShouldForwardCarrierFactory()
    {
        // Arrange
        TestConnectionFactory carrier = new();

        // Act
        AmqpConnectionFactory factory = AmqpConnectionFactory.Create(carrier, new AmqpConnectionFactoryOptions { ContainerId = "client" });

        // Assert
        factory.Capabilities.ShouldBe(carrier.Capabilities);
        factory.ContainerId.ShouldBe("client");
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ConnectAsync: Should name no host when the endpoint is an address")]
    public async Task ConnectAsync_OnIpEndPoint_ShouldSendNoHostName()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TestConnection carrier = new();
        TestConnectionFactory carrierFactory = new();
        carrierFactory.Enqueue(carrier);
        AmqpConnectionFactory factory = AmqpConnectionFactory.Create(carrierFactory);
        await using AmqpTestPeer peer = new(carrier);
        IPEndPoint endPoint = new(IPAddress.Loopback, 5672);

        // Act
        Task<IAmqpConnection> connect = factory.ConnectAsync(endPoint, timeout.Token).AsTask();
        AmqpOpenPerformative open = await peer.OpenAsServerAsync(null, timeout.Token);
        IAmqpConnection connection = await connect;

        // Assert
        open.HostName.ShouldBeNull();
        carrierFactory.LastEndPoint.ShouldBe(endPoint);
        connection.State.ShouldBe(ConnectionState.Open);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ConnectAsync: Should connect a new carrier on every call")]
    public async Task ConnectAsync_OnEachCall_ShouldConnectNewCarrier()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TestConnection firstCarrier = new();
        TestConnection secondCarrier = new();
        TestConnectionFactory carrierFactory = new();
        carrierFactory.Enqueue(firstCarrier);
        carrierFactory.Enqueue(secondCarrier);
        AmqpConnectionFactory factory = AmqpConnectionFactory.Create(carrierFactory);
        await using AmqpTestPeer firstPeer = new(firstCarrier);
        await using AmqpTestPeer secondPeer = new(secondCarrier);

        // Act
        Task<IAmqpConnection> firstConnect = factory.ConnectAsync(AmqpTestHost.EndPoint, timeout.Token).AsTask();
        await firstPeer.OpenAsServerAsync(new AmqpOpenPerformative { ContainerId = "first" }, timeout.Token);
        IAmqpConnection first = await firstConnect;
        Task<IAmqpConnection> secondConnect = factory.ConnectAsync(AmqpTestHost.EndPoint, timeout.Token).AsTask();
        await secondPeer.OpenAsServerAsync(new AmqpOpenPerformative { ContainerId = "second" }, timeout.Token);
        IAmqpConnection second = await secondConnect;

        // Assert
        first.RemoteContainerId.ShouldBe("first");
        second.RemoteContainerId.ShouldBe("second");
        first.ContainerId.ShouldBe(second.ContainerId);
        first.Id.ShouldNotBe(second.Id);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ConnectAsync: Should open one bidirectional stream on a multiplexed carrier")]
    public async Task ConnectAsync_OnMultiplexedCarrier_ShouldOpenBidirectionalStream()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TestConnection stream = new(TestMultiplexedConnection.DefaultCapabilities with { IsMultiplexed = false });
        TestMultiplexedConnection carrier = new();
        carrier.Enqueue(stream);
        TestMultiplexedConnectionFactory carrierFactory = new();
        carrierFactory.Enqueue(carrier);
        AmqpConnectionFactory factory = AmqpConnectionFactory.Create(carrierFactory);
        await using AmqpTestPeer peer = new(stream);

        // Act
        Task<IAmqpConnection> connect = factory.ConnectAsync(AmqpTestHost.EndPoint, timeout.Token).AsTask();
        await peer.OpenAsServerAsync(null, timeout.Token);
        IAmqpConnection connection = await connect;

        // Assert
        carrier.OpenStreamCount.ShouldBe(1);
        carrier.LastOpenedDirection.ShouldBe(ConnectionDirection.Bidirectional);
        connection.State.ShouldBe(ConnectionState.Open);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ConnectAsync: Should release a multiplexed carrier whose establishment fails")]
    public async Task ConnectAsync_OnFailedMultiplexedEstablishment_ShouldReleaseCarrier()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TestConnection stream = new(TestMultiplexedConnection.DefaultCapabilities with { IsMultiplexed = false });
        TestMultiplexedConnection carrier = new();
        carrier.Enqueue(stream);
        TestMultiplexedConnectionFactory carrierFactory = new();
        carrierFactory.Enqueue(carrier);
        AmqpConnectionFactory factory = AmqpConnectionFactory.Create(carrierFactory);
        AmqpTestPeer peer = new(stream);

        // Act
        Task<IAmqpConnection> connect = factory.ConnectAsync(AmqpTestHost.EndPoint, timeout.Token).AsTask();
        await peer.ReadProtocolHeaderAsync(timeout.Token);
        await peer.WriteAsync("HTTP/1.1"u8.ToArray(), timeout.Token);
        await Should.ThrowAsync<AmqpException>(connect);

        // Assert
        carrier.IsDisposed.ShouldBeTrue();
    }
}
