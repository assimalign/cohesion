using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Connections.Udp.Tests;

public class UdpDatagramConnectionTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task SendAsync_ClientToServer_ShouldDeliverPayloadWithSenderEndPoint()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);

        UdpConnectionFactory factory = new();

        await using IDatagramConnection server = factory.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        await using IDatagramConnection client = factory.Connect(server.LocalEndPoint);

        byte[] payload = [1, 2, 3, 4, 5];
        byte[] buffer = new byte[64];

        // Act
        await client.SendAsync(payload, server.LocalEndPoint, cancellation.Token);

        DatagramReceiveResult result = await server.ReceiveAsync(buffer, cancellation.Token);

        // Assert
        result.Received.ShouldBe(payload.Length);
        buffer.AsSpan(0, result.Received).ToArray().ShouldBe(payload);
        result.RemoteEndPoint.ShouldBe(client.LocalEndPoint);
    }

    [Fact]
    public async Task SendAsync_ServerReplyToClient_ShouldDeliverPayload()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);

        UdpConnectionFactory factory = new();

        await using IDatagramConnection server = factory.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        await using IDatagramConnection client = factory.Connect(server.LocalEndPoint);

        byte[] request = [42];
        byte[] reply = [7, 8, 9];
        byte[] buffer = new byte[64];

        await client.SendAsync(request, server.LocalEndPoint, cancellation.Token);

        DatagramReceiveResult received = await server.ReceiveAsync(buffer, cancellation.Token);

        // Act
        await server.SendAsync(reply, received.RemoteEndPoint, cancellation.Token);

        DatagramReceiveResult response = await client.ReceiveAsync(buffer, cancellation.Token);

        // Assert
        response.Received.ShouldBe(reply.Length);
        buffer.AsSpan(0, response.Received).ToArray().ShouldBe(reply);
        response.RemoteEndPoint.ShouldBe(server.LocalEndPoint);
    }

    [Fact]
    public async Task ReceiveAsync_AfterTwoSends_ShouldPreserveMessageBoundaries()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);

        UdpConnectionFactory factory = new();

        await using IDatagramConnection server = factory.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        await using IDatagramConnection client = factory.Connect(server.LocalEndPoint);

        byte[] first = [1, 2, 3];
        byte[] second = [4, 5, 6, 7];
        byte[] buffer = new byte[64];

        // Act
        await client.SendAsync(first, server.LocalEndPoint, cancellation.Token);
        await client.SendAsync(second, server.LocalEndPoint, cancellation.Token);

        DatagramReceiveResult firstResult = await server.ReceiveAsync(buffer, cancellation.Token);
        byte[] firstMessage = buffer.AsSpan(0, firstResult.Received).ToArray();

        DatagramReceiveResult secondResult = await server.ReceiveAsync(buffer, cancellation.Token);
        byte[] secondMessage = buffer.AsSpan(0, secondResult.Received).ToArray();

        // Assert
        // Each receive yields exactly one whole datagram (no coalescing). Loopback delivery is
        // ordered in practice, but only the boundary guarantee is asserted, so the messages are
        // matched by size rather than arrival order.
        (byte[] shorter, byte[] longer) = firstMessage.Length <= secondMessage.Length
            ? (firstMessage, secondMessage)
            : (secondMessage, firstMessage);

        shorter.ShouldBe(first);
        longer.ShouldBe(second);
    }

    [Fact]
    public async Task DisposeAsync_CalledTwice_ShouldBeIdempotent()
    {
        // Arrange
        UdpConnectionFactory factory = new();

        IDatagramConnection connection = factory.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        // Act
        await connection.DisposeAsync();

        Exception? exception = await Record.ExceptionAsync(async () => await connection.DisposeAsync());

        // Assert
        exception.ShouldBeNull();
    }

    [Fact]
    public async Task ReceiveAsync_AfterDispose_ShouldThrowObjectDisposedException()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);

        UdpConnectionFactory factory = new();

        IDatagramConnection connection = factory.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        await connection.DisposeAsync();

        // Act / Assert
        await Should.ThrowAsync<ObjectDisposedException>(
            async () => await connection.ReceiveAsync(new byte[16], cancellation.Token));
    }

    [Fact]
    public async Task SendAsync_AfterDispose_ShouldThrowObjectDisposedException()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);

        UdpConnectionFactory factory = new();

        IDatagramConnection connection = factory.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        EndPoint target = connection.LocalEndPoint;

        await connection.DisposeAsync();

        // Act / Assert
        await Should.ThrowAsync<ObjectDisposedException>(
            async () => await connection.SendAsync(new byte[] { 1 }, target, cancellation.Token));
    }
}
