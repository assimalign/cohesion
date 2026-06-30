using System;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Connections.Tests;

public class GuidedBaseTests
{
    private static readonly EndPoint TestEndPoint = new IPEndPoint(IPAddress.Loopback, 16000);

    [Fact]
    public async Task AcceptAsync_OnConnectionListenerBase_ShouldReturnSameInstanceThroughTypedAndInterfaceCalls()
    {
        // Arrange
        TestConnection first = new();
        TestConnection second = new();
        TestConnectionListener listener = new();
        listener.Enqueue(first);
        listener.Enqueue(second);

        // Act
        Connection typed = await listener.AcceptAsync();
        IConnection viaInterface = await ((IConnectionListener)listener).AcceptAsync();

        // Assert
        typed.ShouldBeSameAs(first);
        viaInterface.ShouldBeSameAs(second);
    }

    [Fact]
    public async Task ConnectAsync_OnConnectionFactoryBase_ShouldReturnSameInstanceThroughTypedAndInterfaceCalls()
    {
        // Arrange
        TestConnection first = new();
        TestConnection second = new();
        TestConnectionFactory factory = new();
        factory.Enqueue(first);
        factory.Enqueue(second);

        // Act
        Connection typed = await factory.ConnectAsync(TestEndPoint);
        IConnection viaInterface = await ((IConnectionFactory)factory).ConnectAsync(TestEndPoint);

        // Assert
        typed.ShouldBeSameAs(first);
        viaInterface.ShouldBeSameAs(second);
    }

    [Fact]
    public async Task OpenStreamAsync_OnMultiplexedConnectionBase_ShouldDefaultToBidirectional()
    {
        // Arrange
        TestConnection stream = new();
        TestMultiplexedConnection connection = new();
        connection.Enqueue(stream);

        // Act
        Connection opened = await connection.OpenStreamAsync();

        // Assert
        connection.LastOpenedDirection.ShouldBe(ConnectionDirection.Bidirectional);
        opened.ShouldBeSameAs(stream);
    }

    [Fact]
    public async Task OpenStreamAsync_OnMultiplexedConnectionInterface_ShouldForwardToTypedImplementation()
    {
        // Arrange
        TestConnection stream = new();
        TestMultiplexedConnection connection = new();
        connection.Enqueue(stream);
        IMultiplexedConnection viaInterface = connection;

        // Act
        IConnection opened = await viaInterface.OpenStreamAsync();

        // Assert
        connection.LastOpenedDirection.ShouldBe(ConnectionDirection.Bidirectional);
        opened.ShouldBeSameAs(stream);
    }

    [Fact]
    public async Task AcceptStreamAsync_OnMultiplexedConnectionInterface_ShouldForwardToTypedImplementation()
    {
        // Arrange
        TestConnection stream = new();
        TestMultiplexedConnection connection = new();
        connection.Enqueue(stream);
        IMultiplexedConnection viaInterface = connection;

        // Act
        IConnection accepted = await viaInterface.AcceptStreamAsync();

        // Assert
        accepted.ShouldBeSameAs(stream);
    }

    [Fact]
    public void Direction_OnConnectionBase_ShouldDefaultToBidirectional()
    {
        // Arrange
        MinimalConnection connection = new();

        // Act
        ConnectionDirection direction = connection.Direction;

        // Assert
        direction.ShouldBe(ConnectionDirection.Bidirectional);
    }

    /// <summary>
    /// A minimal <see cref="Connection"/> that does not override <see cref="Connection.Direction"/>,
    /// proving the guided base supplies the bidirectional default.
    /// </summary>
    private sealed class MinimalConnection : Connection
    {
        private readonly Pipe _pipe = new();

        public override ConnectionId Id => ConnectionId.Empty;

        public override EndPoint? LocalEndPoint => null;

        public override EndPoint? RemoteEndPoint => null;

        public override PipeReader Input => _pipe.Reader;

        public override PipeWriter Output => _pipe.Writer;

        public override ConnectionCapabilities Capabilities => TestConnection.DefaultCapabilities;

        public override ConnectionState State => ConnectionState.Open;

        public override CancellationToken ConnectionClosed => CancellationToken.None;

        public override void Abort(Exception? reason = null)
        {
        }

        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
