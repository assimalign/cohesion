using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// A <see cref="MultiplexedConnection"/> double that yields pre-queued stream connections and
/// records how its carrier stream was acquired (accepted from the peer or opened locally).
/// </summary>
internal sealed class TestMultiplexedConnection : MultiplexedConnection
{
    private readonly Queue<MultiplexedConnectionStream> _pendingStreams = new();
    private readonly List<MultiplexedConnectionStream> _streams = new();
    private readonly Lock _gate = new();
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

    public void Enqueue(MultiplexedConnectionStream stream)
    {
        lock (_gate)
        {
            _pendingStreams.Enqueue(stream);
            _streams.Add(stream);
        }
    }

    public override ConnectionId Id => _id;

    public override EndPoint? LocalEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 5671);

    public override EndPoint? RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 45679);

    public override ConnectionCapabilities Capabilities { get; } = DefaultCapabilities;

    public override ConnectionState State => _state;

    public override CancellationToken ConnectionClosed => _closedSource.Token;

    public override void Abort(long errorCode, Exception? reason = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(errorCode);
        MultiplexedConnectionStream[] streams;
        lock (_gate)
        {
            if (_state is ConnectionState.Aborted or ConnectionState.Closed)
            {
                return;
            }

            IsAborted = true;
            _state = ConnectionState.Aborted;
            streams = _streams.ToArray();
        }

        _closedSource.Cancel();
        foreach (MultiplexedConnectionStream stream in streams)
        {
            stream.Abort(errorCode, reason);
        }
    }

    public override void Abort(Exception? reason = null) => Abort(0, reason);

    public override async ValueTask DisposeAsync()
    {
        MultiplexedConnectionStream[] streams;
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
        foreach (MultiplexedConnectionStream stream in streams)
        {
            await stream.DisposeAsync();
        }
    }

    public override ValueTask<MultiplexedConnectionStream> AcceptStreamAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_state is ConnectionState.Aborted or ConnectionState.Closed)
            {
                return ValueTask.FromException<MultiplexedConnectionStream>(new OperationCanceledException(cancellationToken));
            }

            AcceptStreamCount++;
            return ValueTask.FromResult(_pendingStreams.Dequeue());
        }
    }

    public override ValueTask<MultiplexedConnectionStream> OpenStreamAsync(ConnectionDirection direction = ConnectionDirection.Bidirectional, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_state is ConnectionState.Aborted or ConnectionState.Closed)
            {
                throw new ConnectionAbortedException("The multiplexed connection is closed.");
            }

            OpenStreamCount++;
            LastOpenedDirection = direction;
            return ValueTask.FromResult(_pendingStreams.Dequeue());
        }
    }
}
