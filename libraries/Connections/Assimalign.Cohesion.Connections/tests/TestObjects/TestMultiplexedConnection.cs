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
    private readonly Queue<MultiplexedConnectionStream> _pendingStreams = new();
    private readonly CancellationTokenSource _closedSource = new();
    private readonly ConnectionId _id = ConnectionId.New();
    private readonly List<MultiplexedConnectionStream> _streams = new();
    private ConnectionState _state = ConnectionState.Open;

    public ConnectionDirection? LastOpenedDirection { get; private set; }

    public long? LastAbortCode { get; private set; }

    public void Enqueue(MultiplexedConnectionStream stream)
    {
        _pendingStreams.Enqueue(stream);
        _streams.Add(stream);
    }

    public override ConnectionId Id => _id;

    public override EndPoint? LocalEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 13000);

    public override EndPoint? RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 14000);

    public override ConnectionCapabilities Capabilities { get; } = TestConnection.DefaultCapabilities with
    {
        IsMultiplexed = true
    };

    public override ConnectionState State => _state;

    public override CancellationToken ConnectionClosed => _closedSource.Token;

    public override void Abort(Exception? reason = null) => Abort(0, reason);

    public override void Abort(long errorCode, Exception? reason = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(errorCode);
        if (_state != ConnectionState.Open)
        {
            return;
        }

        LastAbortCode = errorCode;
        _state = ConnectionState.Aborted;
        foreach (MultiplexedConnectionStream stream in _streams)
        {
            stream.Abort(errorCode, reason);
        }

        _closedSource.Cancel();
    }

    public override async ValueTask DisposeAsync()
    {
        if (_state == ConnectionState.Open)
        {
            _state = ConnectionState.Closed;
            _closedSource.Cancel();
        }

        foreach (MultiplexedConnectionStream stream in _streams)
        {
            await stream.DisposeAsync();
        }
    }

    public override ValueTask<MultiplexedConnectionStream> AcceptStreamAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_pendingStreams.Dequeue());

    public override ValueTask<MultiplexedConnectionStream> OpenStreamAsync(ConnectionDirection direction = ConnectionDirection.Bidirectional, CancellationToken cancellationToken = default)
    {
        LastOpenedDirection = direction;

        return ValueTask.FromResult(_pendingStreams.Dequeue());
    }
}
