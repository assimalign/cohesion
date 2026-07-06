using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Connections.InMemory.Tests;

public class InMemoryConnectionListenerTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact(DisplayName = "Cohesion Test [Connections.InMemory] - Listener: Dial then accept should yield a connected pair")]
    public async Task Dial_ThenAccept_ShouldYieldConnectedPair()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using InMemoryConnectionListener listener = new();
        InMemoryConnectionFactory factory = listener.CreateFactory();

        // Act — a pending accept resolves as soon as the factory dials, mirroring a real listener.
        ValueTask<Connection> acceptTask = listener.AcceptAsync(cancellation.Token);

        Connection client = await factory.ConnectAsync(listener.EndPoint, cancellation.Token);
        Connection server = await acceptTask;

        byte[] payload = [7, 8, 9];
        await client.Output.WriteAsync(payload, cancellation.Token);
        byte[] received = await server.Input.ReadExactlyAsync(payload.Length, cancellation.Token);

        // Assert
        received.ShouldBe(payload);
        client.RemoteEndPoint.ShouldBe(listener.EndPoint);
        server.LocalEndPoint.ShouldBe(listener.EndPoint);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Connections.InMemory] - Listener: Accept should re-arm across multiple dials")]
    public async Task Accept_AcrossMultipleDials_ShouldReArm()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using InMemoryConnectionListener listener = new();
        InMemoryConnectionFactory factory = listener.CreateFactory();

        // Act & Assert
        for (int i = 0; i < 3; i++)
        {
            Connection client = await factory.ConnectAsync(listener.EndPoint, cancellation.Token);
            Connection server = await listener.AcceptAsync(cancellation.Token);

            server.ShouldNotBeNull();
            client.Id.ShouldNotBe(server.Id);

            await client.DisposeAsync();
            await server.DisposeAsync();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Connections.InMemory] - Listener: Disposing should cancel a pending accept")]
    public async Task DisposeAsync_WithPendingAccept_ShouldCancelAccept()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        InMemoryConnectionListener listener = new();

        ValueTask<Connection> acceptTask = listener.AcceptAsync(cancellation.Token);

        // Act
        await listener.DisposeAsync();

        // Assert
        await Should.ThrowAsync<OperationCanceledException>(async () => await acceptTask);
    }

    [Fact(DisplayName = "Cohesion Test [Connections.InMemory] - Listener: Dialing a disposed listener should throw")]
    public async Task Connect_AfterDispose_ShouldThrowConnectionAborted()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        InMemoryConnectionListener listener = new();
        InMemoryConnectionFactory factory = listener.CreateFactory();

        await listener.DisposeAsync();

        // Act & Assert
        await Should.ThrowAsync<ConnectionAbortedException>(
            async () => await factory.ConnectAsync(listener.EndPoint, cancellation.Token));
    }

    [Fact(DisplayName = "Cohesion Test [Connections.InMemory] - Listener: Endpoint and capabilities should be honored")]
    public async Task Listener_EndPointAndCapabilities_ShouldBeHonored()
    {
        // Arrange
        InMemoryEndPoint endPoint = new("test-server");
        ConnectionCapabilities capabilities = InMemoryConnectionPair.DefaultCapabilities with
        {
            Security = ConnectionSecurity.Tls
        };

        // Act
        await using InMemoryConnectionListener listener = new(endPoint, capabilities);

        // Assert
        listener.EndPoint.ShouldBe(endPoint);
        listener.Capabilities.Security.ShouldBe(ConnectionSecurity.Tls);
        listener.CreateFactory().Capabilities.Security.ShouldBe(ConnectionSecurity.Tls);
    }
}
