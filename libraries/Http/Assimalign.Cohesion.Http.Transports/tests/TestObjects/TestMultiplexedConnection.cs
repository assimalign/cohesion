using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Transports.Tests.TestObjects;

/// <summary>
/// A <see cref="MultiplexedConnection"/> double that yields pre-queued
/// streams. When the queue empties, <see cref="AcceptStreamAsync"/> throws
/// <see cref="OperationCanceledException"/> so the HTTP/3 receive loop
/// observes a finished connection and completes its enumerable cleanly.
/// </summary>
internal sealed class TestMultiplexedConnection : MultiplexedConnection
{
    private readonly Queue<Connection> _pendingStreams;
    private readonly CancellationTokenSource _closedSource = new();
    private readonly ConnectionId _id = ConnectionId.New();
    private ConnectionState _state = ConnectionState.Open;

    public TestMultiplexedConnection(params Connection[] streams)
    {
        _pendingStreams = new Queue<Connection>(streams);
    }

    public bool IsDisposed { get; private set; }

    public override ConnectionId Id => _id;

    public override EndPoint? LocalEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 13000);

    public override EndPoint? RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 14000);

    public override ConnectionCapabilities Capabilities { get; } = TestConnection.DefaultCapabilities with
    {
        IsMultiplexed = true,
        Security = ConnectionSecurity.Tls
    };

    public override ConnectionState State => _state;

    public override CancellationToken ConnectionClosed => _closedSource.Token;

    public override void Abort(Exception? reason = null)
    {
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
        if (_pendingStreams.Count == 0)
        {
            // No more inbound streams will ever arrive on this double; the
            // receive loop treats cancellation as connection termination.
            return ValueTask.FromException<Connection>(new OperationCanceledException(cancellationToken));
        }

        return ValueTask.FromResult(_pendingStreams.Dequeue());
    }

    public override ValueTask<Connection> OpenStreamAsync(ConnectionDirection direction = ConnectionDirection.Bidirectional, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult<Connection>(new TestConnection(direction: direction));
    }
}
