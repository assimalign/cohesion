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
    private static readonly EndPoint _testEndPoint = new IPEndPoint(IPAddress.Loopback, 16000);

    [Fact(DisplayName = "Cohesion Test [Connections] - BindAsync: Guided listener base should forward through interface calls")]
    public async Task BindAsync_OnConnectionListenerBase_ShouldForwardThroughInterfaceCall()
    {
        // Arrange
        TestConnectionListener listener = new();
        IDuplexConnectionListener viaInterface = listener;

        // Act
        await listener.BindAsync();
        await viaInterface.BindAsync();

        // Assert
        listener.BindCount.ShouldBe(2);
    }

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
        IDuplexConnection typed = await listener.AcceptAsync();
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
        IDuplexConnection typed = await factory.ConnectAsync(_testEndPoint);
        IConnection viaInterface = await ((IConnectionFactory)factory).ConnectAsync(_testEndPoint);

        // Assert
        typed.ShouldBeSameAs(first);
        viaInterface.ShouldBeSameAs(second);
    }

    [Fact(DisplayName = "Cohesion Test [Connections] - OpenStreamAsync: Guided multiplexed base should default to bidirectional")]
    public async Task OpenStreamAsync_OnMultiplexedConnectionBase_ShouldDefaultToBidirectional()
    {
        // Arrange
        TestConnection stream = new();
        TestMultiplexedConnection connection = new();
        connection.Enqueue(stream);

        // Act
        MultiplexedConnectionStream opened = await connection.OpenStreamAsync();

        // Assert
        connection.LastOpenedDirection.ShouldBe(ConnectionDirection.Bidirectional);
        opened.ShouldBeSameAs(stream);
    }

    [Fact(DisplayName = "Cohesion Test [Connections] - OpenStreamAsync: Guided multiplexed base should return the stream through its interface")]
    public async Task OpenStreamAsync_OnMultiplexedConnectionInterface_ShouldForwardToTypedImplementation()
    {
        // Arrange
        TestConnection stream = new();
        TestMultiplexedConnection connection = new();
        connection.Enqueue(stream);
        IMultiplexedConnection viaInterface = connection;

        // Act
        IMultiplexedConnectionStream opened = await viaInterface.OpenStreamAsync();

        // Assert
        connection.LastOpenedDirection.ShouldBe(ConnectionDirection.Bidirectional);
        opened.ShouldBeSameAs(stream);
    }

    [Fact(DisplayName = "Cohesion Test [Connections] - AcceptStreamAsync: Guided multiplexed base should return the stream through its interface")]
    public async Task AcceptStreamAsync_OnMultiplexedConnectionInterface_ShouldForwardToTypedImplementation()
    {
        // Arrange
        TestConnection stream = new();
        TestMultiplexedConnection connection = new();
        connection.Enqueue(stream);
        IMultiplexedConnection viaInterface = connection;

        // Act
        IMultiplexedConnectionStream accepted = await viaInterface.AcceptStreamAsync();

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

    [Fact(DisplayName = "Cohesion Test [Connections] - Abort: Guided multiplexed connection exposes its coded lifecycle through the interface")]
    public async Task Abort_OnMultiplexedConnectionInterface_ShouldEndConnectionAndStreams()
    {
        // Arrange
        await using TestConnection stream = new();
        await using TestMultiplexedConnection connection = new();
        connection.Enqueue(stream);
        IMultiplexedConnection viaInterface = connection;

        // Act
        viaInterface.Abort(42);

        // Assert
        connection.LastAbortCode.ShouldBe(42);
        stream.LastAbortCode.ShouldBe(42);
        viaInterface.State.ShouldBe(ConnectionState.Aborted);
        viaInterface.ConnectionClosed.IsCancellationRequested.ShouldBeTrue();
        stream.ConnectionClosed.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Connections] - Abort: Guided multiplexed stream exposes its coded lifecycle through the interface")]
    public async Task Abort_OnMultiplexedStreamInterface_ShouldEndStream()
    {
        // Arrange
        await using TestConnection stream = new();
        IMultiplexedConnectionStream viaInterface = stream;

        // Act
        viaInterface.Abort(42);

        // Assert
        stream.LastAbortCode.ShouldBe(42);
        viaInterface.State.ShouldBe(ConnectionState.Aborted);
        viaInterface.ConnectionClosed.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Connections] - IsInputCompleted: Guided multiplexed stream exposes conservative completion through the interface")]
    public async Task IsInputCompleted_OnMultiplexedStreamInterface_ShouldReturnImplementationValue()
    {
        // Arrange
        await using TestConnection stream = new();
        IMultiplexedConnectionStream viaInterface = stream;

        // Act
        bool completed = viaInterface.IsInputCompleted;

        // Assert
        completed.ShouldBeFalse();
        viaInterface.Direction.ShouldBe(ConnectionDirection.Bidirectional);
        viaInterface.Features.ShouldBeSameAs(stream.Features);
    }

    /// <summary>
    /// A minimal <see cref="DuplexConnection"/> that does not override <see cref="DuplexConnection.Direction"/>,
    /// proving the guided base supplies the bidirectional default.
    /// </summary>
    private sealed class MinimalConnection : DuplexConnection
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
