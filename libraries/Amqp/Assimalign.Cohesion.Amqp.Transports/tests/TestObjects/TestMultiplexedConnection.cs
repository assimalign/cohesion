using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Transports.Tests;

/// <summary>
/// A <see cref="MultiplexedConnection"/> double that yields pre-queued stream connections and
/// records how its carrier stream was acquired (accepted from the peer or opened locally).
/// </summary>
internal sealed class TestMultiplexedConnection : MultiplexedConnection
{
    private readonly Queue<Connection> _pendingStreams = new();
    private readonly CancellationTokenSource _closedSource = new();
    private readonly ConnectionId _id = ConnectionId.New();
    private ConnectionState _state = ConnectionState.Open;

    /// <summary>
    /// Gets the default capabilities of a multiplexed AMQP carrier: a reliable, ordered,
    /// multiplexed byte stream.
    /// </summary>
    public static ConnectionCapabilities DefaultCapabilities { get; } = TestConnection.DefaultCapabilities with
    {
        IsMultiplexed = true
    };

    public int AcceptStreamCount { get; private set; }

    public int OpenStreamCount { get; private set; }

    public ConnectionDirection? LastOpenedDirection { get; private set; }

    public bool IsAborted { get; private set; }

    public bool IsDisposed { get; private set; }

    public void Enqueue(Connection stream) => _pendingStreams.Enqueue(stream);

    public override ConnectionId Id => _id;

    public override EndPoint? LocalEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 5671);

    public override EndPoint? RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 45679);

    public override ConnectionCapabilities Capabilities { get; } = DefaultCapabilities;

    public override ConnectionState State => _state;

    public override CancellationToken ConnectionClosed => _closedSource.Token;

    public override void Abort(Exception? reason = null)
    {
        IsAborted = true;
        _state = ConnectionState.Aborted;
        _closedSource.Cancel();
    }

    public override ValueTask DisposeAsync()
    {
        IsDisposed = true;
        _state = ConnectionState.Closed;
        _closedSource.Cancel();
        return ValueTask.CompletedTask;
    }

    public override ValueTask<Connection> AcceptStreamAsync(CancellationToken cancellationToken = default)
    {
        AcceptStreamCount++;

        return ValueTask.FromResult(_pendingStreams.Dequeue());
    }

    public override ValueTask<Connection> OpenStreamAsync(ConnectionDirection direction = ConnectionDirection.Bidirectional, CancellationToken cancellationToken = default)
    {
        OpenStreamCount++;
        LastOpenedDirection = direction;

        return ValueTask.FromResult(_pendingStreams.Dequeue());
    }
}
