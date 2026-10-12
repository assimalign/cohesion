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
    private readonly Queue<MultiplexedStream> _pendingStreams;
    private readonly List<TestConnection> _openedStreams = new();
    private readonly List<MultiplexedStream> _streams;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _closedSource = new();
    private readonly ConnectionId _id = ConnectionId.New();
    private ConnectionState _state = ConnectionState.Open;

    public TestMultiplexedConnection(params MultiplexedStream[] streams)
    {
        _pendingStreams = new Queue<MultiplexedStream>(streams);
        _streams = new List<MultiplexedStream>(streams);
    }

    public bool IsDisposed { get; private set; }

    /// <summary>
    /// The reason passed to the first <see cref="Abort"/>, or <see langword="null"/> when the
    /// connection was never aborted. The HTTP/3 engine aborts with an exception carrying the
    /// connection error code, so tests read the intended RFC 9114 §8 code from here.
    /// </summary>
    public Exception? AbortReason { get; private set; }

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

    public override void Abort(long errorCode, Exception? reason = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(errorCode);
        MultiplexedStream[] streams;
        lock (_gate)
        {
            if (_state is ConnectionState.Aborted or ConnectionState.Closed)
            {
                return;
            }

            AbortReason = reason;
            _state = ConnectionState.Aborted;
            streams = _streams.ToArray();
        }

        _closedSource.Cancel();
        foreach (MultiplexedStream stream in streams)
        {
            stream.Abort(errorCode, reason);
        }
    }

    public override void Abort(Exception? reason = null) => Abort(0, reason);

    public override async ValueTask DisposeAsync()
    {
        MultiplexedStream[] streams;
        lock (_gate)
        {
            if (IsDisposed)
            {
                return;
            }

            IsDisposed = true;
            if (_state != ConnectionState.Aborted)
            {
                _state = ConnectionState.Closed;
            }
            streams = _streams.ToArray();
        }

        _closedSource.Cancel();
        foreach (MultiplexedStream stream in streams)
        {
            await stream.DisposeAsync();
        }
    }

    public override ValueTask<MultiplexedStream> AcceptStreamAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_state is ConnectionState.Aborted or ConnectionState.Closed || _pendingStreams.Count == 0)
            {
                // No more inbound streams will arrive; cancellation terminates the receive loop.
                return ValueTask.FromException<MultiplexedStream>(new OperationCanceledException(cancellationToken));
            }

            return ValueTask.FromResult(_pendingStreams.Dequeue());
        }
    }

    public override ValueTask<MultiplexedStream> OpenStreamAsync(ConnectionDirection direction = ConnectionDirection.Bidirectional, CancellationToken cancellationToken = default)
    {
        // Capture the opened outbound stream so tests can inspect what the
        // holder wrote to it (e.g. the server control stream's SETTINGS frame)
        // and observe that it stays open while requests are served.
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_state is ConnectionState.Aborted or ConnectionState.Closed)
            {
                throw new ConnectionAbortedException("The multiplexed connection is closed.");
            }

            TestConnection stream = new(direction: direction);
            _openedStreams.Add(stream);
            _streams.Add(stream);
            return ValueTask.FromResult<MultiplexedStream>(stream);
        }
    }
}
