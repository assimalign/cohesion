using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Connections.Tests;

/// <summary>
/// A <see cref="MultiplexedConnection"/> double that yields pre-queued streams and records the
/// direction passed to <see cref="OpenStreamAsync(ConnectionDirection, CancellationToken)"/>.
/// </summary>
internal sealed class TestMultiplexedConnection : MultiplexedConnection
{
    private readonly Queue<Connection> _pendingStreams = new();
    private readonly CancellationTokenSource _closedSource = new();
    private readonly ConnectionId _id = ConnectionId.New();

    public ConnectionDirection? LastOpenedDirection { get; private set; }

    public void Enqueue(Connection stream) => _pendingStreams.Enqueue(stream);

    public override ConnectionId Id => _id;

    public override EndPoint? LocalEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 13000);

    public override EndPoint? RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 14000);

    public override ConnectionCapabilities Capabilities { get; } = TestConnection.DefaultCapabilities with
    {
        IsMultiplexed = true
    };

    public override ConnectionState State => ConnectionState.Open;

    public override CancellationToken ConnectionClosed => _closedSource.Token;

    public override void Abort(Exception? reason = null) => _closedSource.Cancel();

    public override ValueTask DisposeAsync()
    {
        _closedSource.Cancel();
        return ValueTask.CompletedTask;
    }

    public override ValueTask<Connection> AcceptStreamAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_pendingStreams.Dequeue());

    public override ValueTask<Connection> OpenStreamAsync(ConnectionDirection direction = ConnectionDirection.Bidirectional, CancellationToken cancellationToken = default)
    {
        LastOpenedDirection = direction;

        return ValueTask.FromResult(_pendingStreams.Dequeue());
    }
}
