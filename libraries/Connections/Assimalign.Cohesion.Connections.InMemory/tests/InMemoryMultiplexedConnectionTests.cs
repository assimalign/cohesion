using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Connections.InMemory.Tests;

public class InMemoryMultiplexedConnectionTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact(DisplayName = "Cohesion Test [Connections.InMemory] - Multiplexed: Opening a stream should be accepted by the peer and round-trip")]
    public async Task OpenStream_ShouldBeAcceptedByPeerAndRoundTrip()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        (MultiplexedConnection client, MultiplexedConnection server) = InMemoryMultiplexedConnectionPair.Create();

        // Act
        Connection clientStream = await client.OpenStreamAsync(cancellationToken: cancellation.Token);
        Connection serverStream = await server.AcceptStreamAsync(cancellation.Token);

        byte[] payload = [4, 5, 6];
        await clientStream.Output.WriteAsync(payload, cancellation.Token);
        byte[] received = await serverStream.Input.ReadExactlyAsync(payload.Length, cancellation.Token);

        // Assert
        received.ShouldBe(payload);
        clientStream.Capabilities.IsMultiplexed.ShouldBeFalse();
        client.Capabilities.IsMultiplexed.ShouldBeTrue();

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Connections.InMemory] - Multiplexed: Multiple streams should be independently accepted")]
    public async Task OpenStream_MultipleStreams_ShouldBeIndependentlyAccepted()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        (MultiplexedConnection client, MultiplexedConnection server) = InMemoryMultiplexedConnectionPair.Create();

        // Act
        Connection first = await client.OpenStreamAsync(cancellationToken: cancellation.Token);
        Connection second = await client.OpenStreamAsync(cancellationToken: cancellation.Token);

        Connection serverFirst = await server.AcceptStreamAsync(cancellation.Token);
        Connection serverSecond = await server.AcceptStreamAsync(cancellation.Token);

        // Assert
        first.Id.ShouldNotBe(second.Id);
        serverFirst.Id.ShouldNotBe(serverSecond.Id);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Connections.InMemory] - Multiplexed: An outbound unidirectional stream should be write-only for the opener and read-only for the peer")]
    public async Task OpenStream_WriteOnly_ShouldMirrorAsReadOnlyOnPeer()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        (MultiplexedConnection client, MultiplexedConnection server) = InMemoryMultiplexedConnectionPair.Create();

        // Act
        Connection clientStream = await client.OpenStreamAsync(ConnectionDirection.WriteOnly, cancellation.Token);
        Connection serverStream = await server.AcceptStreamAsync(cancellation.Token);

        byte[] payload = [1, 1, 2, 3, 5];
        await clientStream.Output.WriteAsync(payload, cancellation.Token);
        byte[] received = await serverStream.Input.ReadExactlyAsync(payload.Length, cancellation.Token);

        // Assert
        clientStream.Direction.ShouldBe(ConnectionDirection.WriteOnly);
        serverStream.Direction.ShouldBe(ConnectionDirection.ReadOnly);
        received.ShouldBe(payload);

        // The read-only peer end must reject writes.
        Should.Throw<InvalidOperationException>(() => serverStream.Output.GetMemory());

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Connections.InMemory] - Multiplexed: Opening a read-only stream should throw")]
    public async Task OpenStream_ReadOnly_ShouldThrowArgumentException()
    {
        // Arrange
        (MultiplexedConnection client, MultiplexedConnection _) = InMemoryMultiplexedConnectionPair.Create();

        // Act & Assert
        await Should.ThrowAsync<ArgumentException>(
            async () => await client.OpenStreamAsync(ConnectionDirection.ReadOnly));

        await client.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Connections.InMemory] - Multiplexed: Accepting after the connection closes should throw OperationCanceled")]
    public async Task AcceptStream_AfterDispose_ShouldThrowOperationCanceled()
    {
        // Arrange
        (MultiplexedConnection client, MultiplexedConnection _) = InMemoryMultiplexedConnectionPair.Create();

        // Act
        await client.DisposeAsync();

        // Assert
        client.State.ShouldBe(ConnectionState.Closed);
        await Should.ThrowAsync<OperationCanceledException>(async () => await client.AcceptStreamAsync());
    }

    [Fact(DisplayName = "Cohesion Test [Connections.InMemory] - Multiplexed: Listener dial then accept should yield a connected multiplexed pair")]
    public async Task Dial_ThenAccept_ShouldYieldMultiplexedPair()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using InMemoryMultiplexedConnectionListener listener = new();
        InMemoryMultiplexedConnectionFactory factory = listener.CreateFactory();

        // Act
        ValueTask<MultiplexedConnection> acceptTask = listener.AcceptAsync(cancellation.Token);

        MultiplexedConnection client = await factory.ConnectAsync(listener.EndPoint, cancellation.Token);
        MultiplexedConnection server = await acceptTask;

        Connection clientStream = await client.OpenStreamAsync(cancellationToken: cancellation.Token);
        Connection serverStream = await server.AcceptStreamAsync(cancellation.Token);

        byte[] payload = [42];
        await clientStream.Output.WriteAsync(payload, cancellation.Token);
        byte[] received = await serverStream.Input.ReadExactlyAsync(1, cancellation.Token);

        // Assert
        received.ShouldBe(payload);
        client.Capabilities.IsMultiplexed.ShouldBeTrue();

        await client.DisposeAsync();
        await server.DisposeAsync();
    }
}
