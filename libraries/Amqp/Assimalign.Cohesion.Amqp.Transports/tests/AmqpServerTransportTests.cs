using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Transports.Tests;

public class AmqpServerTransportTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    private static AmqpTransportOptions ManualNegotiationOptions => new()
    {
        AutoNegotiateProtocolHeader = false
    };

    [Fact(DisplayName = "Cohesion Test [Amqp.Transports] - AcceptAsync: Should yield a tracked AMQP connection over a single-stream carrier")]
    public async Task AcceptAsync_OnSingleStreamCarrier_ShouldYieldTrackedAmqpConnection()
    {
        // Arrange
        TestConnection carrier = new();
        TestConnectionListener listener = new();
        listener.Enqueue(carrier);
        await using AmqpServerTransport transport = new(listener);

        // Act
        AmqpConnection connection = await transport.AcceptAsync();

        // Assert
        connection.ShouldNotBeNull();
        connection.Id.ShouldBe(carrier.Id);
        connection.State.ShouldBe(ConnectionState.Open);
        transport.Connections.ShouldHaveSingleItem().ShouldBeSameAs(connection);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Transports] - OpenAsync: Should wire the context to the carrier connection's pipes")]
    public async Task OpenAsync_OnSingleStreamCarrier_ShouldWireContextToCarrierPipes()
    {
        // Arrange
        using CancellationTokenSource timeout = new(TestTimeout);
        TestConnection carrier = new();
        TestConnectionListener listener = new();
        listener.Enqueue(carrier);
        await using AmqpServerTransport transport = new(listener, ManualNegotiationOptions);
        AmqpConnection connection = await transport.AcceptAsync(timeout.Token);

        // Act
        AmqpConnectionContext context = await connection.OpenAsync(timeout.Token);

        await context.Output.WriteAsync(new byte[] { 0x01, 0x02, 0x03 }, timeout.Token);
        byte[] observedByPeer = await carrier.ReadBufferedPeerBytesAsync(timeout.Token);

        await carrier.WritePeerAsync(new byte[] { 0x0a, 0x0b }, timeout.Token);
        ReadResult inbound = await context.Input.ReadAsync(timeout.Token);
        byte[] observedByContext = inbound.Buffer.ToArray();
        context.Input.AdvanceTo(inbound.Buffer.End);

        // Assert
        observedByPeer.ShouldBe(new byte[] { 0x01, 0x02, 0x03 });
        observedByContext.ShouldBe(new byte[] { 0x0a, 0x0b });
        context.LocalEndPoint.ShouldBeSameAs(carrier.LocalEndPoint);
        context.RemoteEndPoint.ShouldBeSameAs(carrier.RemoteEndPoint);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Transports] - OpenAsync: Should return the same context on repeated opens")]
    public async Task OpenAsync_OnRepeatedCalls_ShouldReturnSameContext()
    {
        // Arrange
        using CancellationTokenSource timeout = new(TestTimeout);
        TestConnection carrier = new();
        TestConnectionListener listener = new();
        listener.Enqueue(carrier);
        await using AmqpServerTransport transport = new(listener, ManualNegotiationOptions);
        AmqpConnection connection = await transport.AcceptAsync(timeout.Token);

        // Act
        AmqpConnectionContext first = await connection.OpenAsync(timeout.Token);
        AmqpConnectionContext second = await connection.OpenAsync(timeout.Token);
        AmqpConnectionContext synchronous = connection.Open();

        // Assert
        second.ShouldBeSameAs(first);
        synchronous.ShouldBeSameAs(first);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Transports] - AcceptAsync: Should accept the carrier stream from the peer on a multiplexed carrier")]
    public async Task AcceptAsync_OnMultiplexedCarrier_ShouldAcceptCarrierStreamFromPeer()
    {
        // Arrange
        using CancellationTokenSource timeout = new(TestTimeout);
        TestConnection stream = new();
        TestMultiplexedConnection carrier = new();
        carrier.Enqueue(stream);
        TestMultiplexedConnectionListener listener = new();
        listener.Enqueue(carrier);
        await using AmqpServerTransport transport = new(listener, ManualNegotiationOptions);

        // Act
        AmqpConnection connection = await transport.AcceptAsync(timeout.Token);
        AmqpConnectionContext context = await connection.OpenAsync(timeout.Token);

        await context.Output.WriteAsync(new byte[] { 0x42 }, timeout.Token);
        byte[] observedOnStream = await stream.ReadBufferedPeerBytesAsync(timeout.Token);

        // Assert
        connection.Id.ShouldBe(carrier.Id);
        carrier.AcceptStreamCount.ShouldBe(1);
        carrier.OpenStreamCount.ShouldBe(0);
        observedOnStream.ShouldBe(new byte[] { 0x42 });
        context.LocalEndPoint.ShouldBeSameAs(stream.LocalEndPoint);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Transports] - AcceptAsync: Should throw after the transport is disposed")]
    public async Task AcceptAsync_AfterDispose_ShouldThrowObjectDisposedException()
    {
        // Arrange
        TestConnectionListener listener = new();
        AmqpServerTransport transport = new(listener);
        await transport.DisposeAsync();

        // Act + Assert
        await Should.ThrowAsync<ObjectDisposedException>(async () => await transport.AcceptAsync());
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Transports] - DisposeAsync: Should dispose tracked connections and the owned listener")]
    public async Task DisposeAsync_OnServerTransport_ShouldDisposeTrackedConnectionsAndListener()
    {
        // Arrange
        TestConnection carrier = new();
        TestConnectionListener listener = new();
        listener.Enqueue(carrier);
        AmqpServerTransport transport = new(listener);
        await transport.AcceptAsync();

        // Act
        await transport.DisposeAsync();

        // Assert
        carrier.IsDisposed.ShouldBeTrue();
        listener.IsDisposed.ShouldBeTrue();
        transport.Connections.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Transports] - DisposeAsync: Should untrack the connection and tear down its carrier")]
    public async Task DisposeAsync_OnAcceptedConnection_ShouldUntrackConnectionAndDisposeCarrier()
    {
        // Arrange
        TestConnection carrier = new();
        TestConnectionListener listener = new();
        listener.Enqueue(carrier);
        await using AmqpServerTransport transport = new(listener);
        AmqpConnection connection = await transport.AcceptAsync();

        // Act
        await connection.DisposeAsync();

        // Assert
        carrier.IsDisposed.ShouldBeTrue();
        transport.Connections.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Transports] - Abort: Should abort the carrier connection")]
    public async Task Abort_OnAcceptedConnection_ShouldAbortCarrier()
    {
        // Arrange
        TestConnection carrier = new();
        TestConnectionListener listener = new();
        listener.Enqueue(carrier);
        await using AmqpServerTransport transport = new(listener);
        AmqpConnection connection = await transport.AcceptAsync();
        InvalidOperationException reason = new("test abort");

        // Act
        connection.Abort(reason);

        // Assert
        carrier.IsAborted.ShouldBeTrue();
        carrier.AbortReason.ShouldBeSameAs(reason);
        connection.State.ShouldBe(ConnectionState.Aborted);
        connection.ConnectionClosed.IsCancellationRequested.ShouldBeTrue();
    }
}
