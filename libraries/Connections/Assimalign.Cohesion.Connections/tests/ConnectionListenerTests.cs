using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Connections.Tests;

public class ConnectionListenerTests
{
    [Fact(DisplayName = "Cohesion Test [Connections] - AcceptAsync: Typed and root accepts drain one queue and expose one live set")]
    public async Task AcceptAsync_TypedAndRootCalls_ShouldDrainOneQueueAndShareCollection()
    {
        // Arrange
        await using var listener = new QueueListener();
        await using var first = new RootConnection();
        await using var second = new RootConnection();
        listener.Enqueue(first);
        listener.Enqueue(second);
        IConnectionListener root = listener;
        IConnectionListener<RootConnection> typed = listener;

        // Act
        RootConnection acceptedFirst = await typed.AcceptAsync(CancellationToken.None);
        IConnection acceptedSecond = await root.AcceptAsync(CancellationToken.None);

        // Assert
        acceptedFirst.ShouldBeSameAs(first);
        acceptedSecond.ShouldBeSameAs(second);
        root.Connections.ShouldBeSameAs(typed.Connections);
        root.Connections.Count.ShouldBe(2);
        root.Connections.ShouldContain(first);
        root.Connections.ShouldContain(second);
    }

    [Theory(DisplayName = "Cohesion Test [Connections] - Connections: Every lifetime end removes the accepted connection")]
    [InlineData("close")]
    [InlineData("abort")]
    [InlineData("dispose")]
    [InlineData("peer close")]
    public async Task Connections_LifetimeEnds_ShouldRemoveEntry(string ending)
    {
        // Arrange
        await using var listener = new QueueListener();
        await using var connection = new RootConnection();
        listener.Enqueue(connection);
        await listener.AcceptAsync(CancellationToken.None);
        listener.Connections.Count.ShouldBe(1);

        // Act
        switch (ending)
        {
            case "abort": connection.Abort(); break;
            case "dispose": await connection.DisposeAsync(); break;
            case "peer close": connection.PeerClose(); break;
            default: connection.Close(); break;
        }

        // Assert
        listener.Connections.Count.ShouldBe(0);
        listener.Connections.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Connections] - AcceptAsync: A connection closed before hand-out never remains listed")]
    public async Task AcceptAsync_AlreadyClosed_ShouldNotLeaveEntry()
    {
        // Arrange
        await using var listener = new QueueListener();
        await using var connection = new RootConnection();
        connection.Close();
        listener.Enqueue(connection);

        // Act
        IConnection accepted = await ((IConnectionListener)listener).AcceptAsync(CancellationToken.None);

        // Assert
        accepted.ShouldBeSameAs(connection);
        listener.Connections.Count.ShouldBe(0);
        listener.Connections.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Connections] - Connections: Enumeration tolerates concurrent accepts and closures")]
    public async Task Connections_ConcurrentChurn_ShouldEnumerateAndFinishWithExactCount()
    {
        // Arrange
        await using var listener = new QueueListener();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var finished = new CancellationTokenSource();
        var enumerating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task observer = Task.Run(() =>
        {
            enumerating.SetResult();
            while (!finished.IsCancellationRequested)
            {
                foreach (RootConnection connection in listener.Connections)
                {
                    connection.ShouldNotBeNull();
                }
                listener.Connections.Count.ShouldBeGreaterThanOrEqualTo(0);
            }
        }, timeout.Token);
        await enumerating.Task.WaitAsync(timeout.Token);

        // Act
        try
        {
            await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
            {
                for (int i = 0; i < 250; i++)
                {
                    listener.Enqueue(new RootConnection());
                    RootConnection connection = await listener.AcceptAsync(timeout.Token);
                    await connection.DisposeAsync();
                }
            }, timeout.Token)));
        }
        finally
        {
            finished.Cancel();
            await observer.WaitAsync(timeout.Token);
        }

        // Assert
        listener.Connections.Count.ShouldBe(0);
        listener.Connections.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Connections] - DisposeAsync: Listener disposal preserves handed-out connections")]
    public async Task DisposeAsync_AcceptedConnection_ShouldKeepOwnerLifetime()
    {
        // Arrange
        var listener = new QueueListener();
        await using var connection = new RootConnection();
        listener.Enqueue(connection);
        await listener.AcceptAsync(CancellationToken.None);

        // Act
        await listener.DisposeAsync();

        // Assert
        connection.ConnectionClosed.IsCancellationRequested.ShouldBeFalse();
        listener.Connections.Count.ShouldBe(1);
        await connection.DisposeAsync();
        listener.Connections.Count.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Connections] - Id: Layered listener retains its bound endpoint identity")]
    public async Task Id_LayeredListener_ShouldMatchInnerListener()
    {
        // Arrange
        var inner = new TestConnectionListener();
        await using IDuplexConnectionListener layered = inner.Use(new RecordingConnectionLayer("identity", new List<string>()));

        // Act
        ListenerId id = layered.Id;

        // Assert
        id.ShouldBe(inner.Id);
        id.ShouldNotBe(ListenerId.Empty);
    }

    private sealed class QueueListener : ConnectionListener<RootConnection>
    {
        private readonly Channel<RootConnection> _queue = Channel.CreateUnbounded<RootConnection>();
        public override ListenerId Id { get; } = ListenerId.New();
        public void Enqueue(RootConnection connection) => _queue.Writer.TryWrite(connection);
        protected override ValueTask<RootConnection> AcceptCoreAsync(CancellationToken cancellationToken = default)
            => _queue.Reader.ReadAsync(cancellationToken);
        public override ValueTask DisposeAsync()
        {
            _queue.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RootConnection : Connection
    {
        private readonly CancellationTokenSource _closed = new();
        public override ConnectionId Id => ConnectionId.Empty;
        public override EndPoint? LocalEndPoint => null;
        public override EndPoint? RemoteEndPoint => null;
        public override ConnectionCapabilities Capabilities => default;
        public override ConnectionState State => _closed.IsCancellationRequested ? ConnectionState.Closed : ConnectionState.Open;
        public override CancellationToken ConnectionClosed => _closed.Token;
        public override void Abort(Exception? reason = null) => _closed.Cancel();
        public void Close() => _closed.Cancel();
        public void PeerClose() => _closed.Cancel();
        public override ValueTask DisposeAsync()
        {
            _closed.Cancel();
            return ValueTask.CompletedTask;
        }
    }
}
