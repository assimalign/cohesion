using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Transports.Tests;

public class AmqpClientTransportTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    private static readonly EndPoint TestEndPoint = new IPEndPoint(IPAddress.Loopback, 5672);

    private static AmqpTransportOptions ManualNegotiationOptions => new()
    {
        AutoNegotiateProtocolHeader = false
    };

    [Fact(DisplayName = "Cohesion Test [Amqp.Transports] - ConnectAsync: Should yield a tracked AMQP connection over a single-stream carrier")]
    public async Task ConnectAsync_OnSingleStreamCarrier_ShouldYieldTrackedAmqpConnection()
    {
        // Arrange
        TestConnection carrier = new();
        TestConnectionFactory factory = new();
        factory.Enqueue(carrier);
        await using AmqpClientTransport transport = new(factory, TestEndPoint);

        // Act
        AmqpConnection connection = await transport.ConnectAsync();

        // Assert
        connection.Id.ShouldBe(carrier.Id);
        factory.LastEndPoint.ShouldBeSameAs(TestEndPoint);
        transport.EndPoint.ShouldBeSameAs(TestEndPoint);
        transport.Connections.ShouldHaveSingleItem().ShouldBeSameAs(connection);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Transports] - OpenAsync: Should open a bidirectional carrier stream on a multiplexed carrier")]
    public async Task OpenAsync_OnMultiplexedCarrier_ShouldOpenBidirectionalCarrierStream()
    {
        // Arrange
        using CancellationTokenSource timeout = new(TestTimeout);
        TestConnection stream = new();
        TestMultiplexedConnection carrier = new();
        carrier.Enqueue(stream);
        TestMultiplexedConnectionFactory factory = new();
        factory.Enqueue(carrier);
        await using AmqpClientTransport transport = new(factory, TestEndPoint, ManualNegotiationOptions);
        AmqpConnection connection = await transport.ConnectAsync(timeout.Token);

        // Act
        AmqpConnectionContext context = await connection.OpenAsync(timeout.Token);

        await context.Output.WriteAsync(new byte[] { 0x10, 0x20 }, timeout.Token);
        byte[] observedOnStream = await stream.ReadBufferedPeerBytesAsync(timeout.Token);

        // Assert
        carrier.OpenStreamCount.ShouldBe(1);
        carrier.AcceptStreamCount.ShouldBe(0);
        carrier.LastOpenedDirection.ShouldBe(ConnectionDirection.Bidirectional);
        observedOnStream.ShouldBe(new byte[] { 0x10, 0x20 });
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Transports] - ConnectAsync: Should throw after the transport is disposed")]
    public async Task ConnectAsync_AfterDispose_ShouldThrowObjectDisposedException()
    {
        // Arrange
        TestConnectionFactory factory = new();
        AmqpClientTransport transport = new(factory, TestEndPoint);
        await transport.DisposeAsync();

        // Act + Assert
        await Should.ThrowAsync<ObjectDisposedException>(async () => await transport.ConnectAsync());
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Transports] - DisposeAsync: Should dispose tracked client connections")]
    public async Task DisposeAsync_OnClientTransport_ShouldDisposeTrackedConnections()
    {
        // Arrange
        TestConnection carrier = new();
        TestConnectionFactory factory = new();
        factory.Enqueue(carrier);
        AmqpClientTransport transport = new(factory, TestEndPoint);
        await transport.ConnectAsync();

        // Act
        await transport.DisposeAsync();

        // Assert
        carrier.IsDisposed.ShouldBeTrue();
        transport.Connections.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Transports] - DisposeAsync: Should tear down the carrier stream and the multiplexed carrier")]
    public async Task DisposeAsync_OnMultiplexedConnection_ShouldDisposeCarrierStreamAndConnection()
    {
        // Arrange
        using CancellationTokenSource timeout = new(TestTimeout);
        TestConnection stream = new();
        TestMultiplexedConnection carrier = new();
        carrier.Enqueue(stream);
        TestMultiplexedConnectionFactory factory = new();
        factory.Enqueue(carrier);
        await using AmqpClientTransport transport = new(factory, TestEndPoint, ManualNegotiationOptions);
        AmqpConnection connection = await transport.ConnectAsync(timeout.Token);
        await connection.OpenAsync(timeout.Token);

        // Act
        await connection.DisposeAsync();

        // Assert
        stream.IsDisposed.ShouldBeTrue();
        carrier.IsDisposed.ShouldBeTrue();
        transport.Connections.ShouldBeEmpty();
    }
}
