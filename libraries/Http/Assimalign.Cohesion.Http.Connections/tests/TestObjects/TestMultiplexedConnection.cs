using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

/// <summary>
/// A <see cref="MultiplexedConnection"/> double that yields pre-queued
/// streams. When the queue empties, <see cref="AcceptStreamAsync"/> throws
/// <see cref="OperationCanceledException"/> so the HTTP/3 receive loop
/// observes a finished connection and completes its enumerable cleanly.
/// </summary>
internal sealed class TestMultiplexedConnection : MultiplexedConnection
{
    private readonly Queue<Connection> _pendingStreams;
    private readonly List<TestConnection> _openedStreams = new();
    private readonly CancellationTokenSource _closedSource = new();
    private readonly ConnectionId _id = ConnectionId.New();
    private ConnectionState _state = ConnectionState.Open;

    public TestMultiplexedConnection(params Connection[] streams)
    {
        _pendingStreams = new Queue<Connection>(streams);
    }

    public bool IsDisposed { get; private set; }

    /// <summary>
    /// The outbound streams the holder opened via <see cref="OpenStreamAsync"/>,
    /// in the order they were opened. The HTTP/3 engine opens its control stream
    /// first, so <see cref="ControlStream"/> is the first entry.
    /// </summary>
    public IReadOnlyList<TestConnection> OpenedStreams => _openedStreams;

    /// <summary>
    /// The first outbound stream opened by the holder — the server's HTTP/3
    /// control stream — or <see langword="null"/> if none has been opened yet.
    /// </summary>
    public TestConnection? ControlStream => _openedStreams.Count > 0 ? _openedStreams[0] : null;

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
        // Capture the opened outbound stream so tests can inspect what the
        // holder wrote to it (e.g. the server control stream's SETTINGS frame)
        // and observe that it stays open while requests are served.
        TestConnection stream = new(direction: direction);
        _openedStreams.Add(stream);
        return ValueTask.FromResult<Connection>(stream);
    }
}
