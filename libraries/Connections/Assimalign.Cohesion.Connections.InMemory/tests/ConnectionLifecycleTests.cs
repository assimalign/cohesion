using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Connections.InMemory.Tests;

public class ConnectionLifecycleTests
{
    [Fact(DisplayName = "Cohesion Test [Connections.InMemory] - Complete: Two clean FINs close both stream lifecycles")]
    public async Task Complete_BothPeerOutputs_ShouldNotifyBothEnds()
    {
        // Arrange
        (MultiplexedConnection client, MultiplexedConnection server) = InMemoryMultiplexedConnectionPair.Create();
        await using MultiplexedConnection clientLifetime = client;
        await using MultiplexedConnection serverLifetime = server;
        await using MultiplexedConnectionStream local = await client.OpenStreamAsync();
        await using MultiplexedConnectionStream remote = await server.AcceptStreamAsync();

        // Act
        await local.Output.CompleteAsync();
        local.ConnectionClosed.IsCancellationRequested.ShouldBeFalse();
        remote.ConnectionClosed.IsCancellationRequested.ShouldBeFalse();
        await remote.Output.CompleteAsync();

        // Assert
        local.ConnectionClosed.IsCancellationRequested.ShouldBeTrue();
        remote.ConnectionClosed.IsCancellationRequested.ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Connections.InMemory] - Complete: Ending every usable stream direction signals both endpoints")]
    [InlineData(ConnectionDirection.Bidirectional, false)]
    [InlineData(ConnectionDirection.Bidirectional, true)]
    [InlineData(ConnectionDirection.WriteOnly, false)]
    [InlineData(ConnectionDirection.WriteOnly, true)]
    public async Task Complete_AllUsableStreamHalves_ShouldNotifyBothEnds(ConnectionDirection direction, bool closeServer)
    {
        // Arrange
        (MultiplexedConnection client, MultiplexedConnection server) = InMemoryMultiplexedConnectionPair.Create();
        await using MultiplexedConnection clientLifetime = client;
        await using MultiplexedConnection serverLifetime = server;
        await using MultiplexedConnectionStream local = await client.OpenStreamAsync(direction);
        await using MultiplexedConnectionStream remote = await server.AcceptStreamAsync();
        MultiplexedConnectionStream closing = closeServer ? remote : local;
        MultiplexedConnectionStream peer = closeServer ? local : remote;

        // Act
        if (closing.Direction != ConnectionDirection.ReadOnly)
        {
            await closing.Output.CompleteAsync();
        }
        if (closing.Direction == ConnectionDirection.Bidirectional)
        {
            closing.ConnectionClosed.IsCancellationRequested.ShouldBeFalse();
            peer.ConnectionClosed.IsCancellationRequested.ShouldBeFalse();
        }
        if (closing.Direction != ConnectionDirection.WriteOnly)
        {
            await closing.Input.CompleteAsync();
        }

        // Assert
        closing.ConnectionClosed.IsCancellationRequested.ShouldBeTrue();
        peer.ConnectionClosed.IsCancellationRequested.ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Connections.InMemory] - ConnectionClosed: Every connection shape signals both endpoints before disposal returns")]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task ConnectionClosed_OnDisposeOrAbort_ShouldNotifyBothEnds(int shape, bool abort)
    {
        // Arrange
        (MultiplexedConnection parentClient, MultiplexedConnection parentServer) = InMemoryMultiplexedConnectionPair.Create();
        await using MultiplexedConnection parentClientLifetime = parentClient;
        await using MultiplexedConnection parentServerLifetime = parentServer;
        IConnection client;
        IConnection server;
        if (shape == 0)
        {
            (client, server) = InMemoryConnectionPair.Create();
        }
        else if (shape == 1)
        {
            client = parentClient;
            server = parentServer;
        }
        else
        {
            client = await parentClient.OpenStreamAsync(cancellationToken: CancellationToken.None);
            server = await parentServer.AcceptStreamAsync(CancellationToken.None);
        }
        await using IConnection clientLifetime = client;
        await using IConnection serverLifetime = server;

        // Act
        if (abort)
        {
            client.Abort();
            client.ConnectionClosed.IsCancellationRequested.ShouldBeTrue();
        }
        await client.DisposeAsync();
        client.ConnectionClosed.IsCancellationRequested.ShouldBeTrue();

        // Assert
        client.ConnectionClosed.IsCancellationRequested.ShouldBeTrue();
        server.ConnectionClosed.IsCancellationRequested.ShouldBeTrue();
        await server.DisposeAsync();
        server.ConnectionClosed.IsCancellationRequested.ShouldBeTrue();
    }
}
