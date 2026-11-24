using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// A <see cref="MultiplexedConnection"/> double whose streams are the ones a test queues. Accepting a
/// stream waits, like a real connection, until one is queued, the accept is canceled, or the connection
/// ends; opening one takes the next queued stream at once. It records how its streams were acquired and
/// how it ended, and ends every stream it handed out when it is aborted or disposed.
/// </summary>
internal sealed class TestMultiplexedConnection : MultiplexedConnection
{
    private readonly Channel<MultiplexedConnectionStream> _pendingStreams = Channel.CreateUnbounded<MultiplexedConnectionStream>();
    private readonly List<MultiplexedConnectionStream> _streams = new();
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _closedSource = new();
    private readonly SemaphoreSlim _acceptStreamCalls = new(0);
    private readonly ConnectionId _id = ConnectionId.New();
    private ConnectionState _state = ConnectionState.Open;
    private ConnectionDirection? _lastOpenedDirection;
    private int _acceptStreamCount;
    private int _openStreamCount;
    private bool _isAborted;
    private bool _isDisposed;

    /// <summary>
    /// Gets the default capabilities of a multiplexed AMQP carrier: a reliable, ordered,
    /// multiplexed byte stream.
    /// </summary>
    public static ConnectionCapabilities DefaultCapabilities { get; } = TestConnection.DefaultCapabilities with
    {
        IsMultiplexed = true
    };

    public int AcceptStreamCount => Volatile.Read(ref _acceptStreamCount);

    public int OpenStreamCount => Volatile.Read(ref _openStreamCount);

    public ConnectionDirection? LastOpenedDirection
    {
        get
        {
            lock (_gate)
            {
                return _lastOpenedDirection;
            }
        }
    }

    public bool IsAborted
    {
        get
        {
            lock (_gate)
            {
                return _isAborted;
            }
        }
    }

    public bool IsDisposed
    {
        get
        {
            lock (_gate)
            {
                return _isDisposed;
            }
        }
    }

    public override ConnectionId Id => _id;

    public override EndPoint? LocalEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 5671);

    public override EndPoint? RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 45679);

    public override ConnectionCapabilities Capabilities { get; } = DefaultCapabilities;

    public override ConnectionState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public override CancellationToken ConnectionClosed => _closedSource.Token;

    /// <summary>
    /// Queues a stream for the next accept or open.
    /// </summary>
    public void Enqueue(MultiplexedConnectionStream stream)
    {
        lock (_gate)
        {
            _streams.Add(stream);
        }

        if (!_pendingStreams.Writer.TryWrite(stream))
        {
            throw new InvalidOperationException("The test multiplexed connection has ended.");
        }
    }

    /// <summary>
    /// Returns a task that completes once a caller has asked for the peer's next stream.
    /// </summary>
    public Task WaitForAcceptStreamAsync(CancellationToken cancellationToken = default)
    {
        return _acceptStreamCalls.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Returns a task that completes when the connection's lifetime ends (abort or disposal).
    /// </summary>
    public Task WhenClosedAsync()
    {
        TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConnectionClosed.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), closed);
        return closed.Task;
    }

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

            _isAborted = true;
            _state = ConnectionState.Aborted;
            streams = _streams.ToArray();
        }

        _pendingStreams.Writer.TryComplete();
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
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;

            if (_state != ConnectionState.Aborted)
            {
                _state = ConnectionState.Closed;
            }

            streams = _streams.ToArray();
        }

        _pendingStreams.Writer.TryComplete();
        _closedSource.Cancel();

        foreach (MultiplexedConnectionStream stream in streams)
        {
            await stream.DisposeAsync();
        }
    }

    public override async ValueTask<MultiplexedConnectionStream> AcceptStreamAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _acceptStreamCount);
        _acceptStreamCalls.Release();

        try
        {
            return await _pendingStreams.Reader.ReadAsync(cancellationToken);
        }
        catch (ChannelClosedException)
        {
            throw new ConnectionAbortedException("The test multiplexed connection has ended.");
        }
    }

    public override ValueTask<MultiplexedConnectionStream> OpenStreamAsync(ConnectionDirection direction = ConnectionDirection.Bidirectional, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _openStreamCount);

        lock (_gate)
        {
            _lastOpenedDirection = direction;

            if (_state is ConnectionState.Aborted or ConnectionState.Closed)
            {
                throw new ConnectionAbortedException("The test multiplexed connection has ended.");
            }
        }

        if (!_pendingStreams.Reader.TryRead(out MultiplexedConnectionStream? stream))
        {
            throw new InvalidOperationException("No stream is queued for the test multiplexed connection to open.");
        }

        return ValueTask.FromResult(stream);
    }
}
