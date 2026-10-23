using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Connections.InMemory.Internal;

/// <summary>
/// One end of a cross-wired, in-memory <see cref="MultiplexedConnection"/>. Streams opened on one end
/// are delivered to the other end's accept queue; each stream is itself an in-memory
/// <see cref="MultiplexedConnectionStream"/>, so h2/h3-shaped tests can open and accept multiple concurrent streams.
/// </summary>
/// <remarks>
/// Opening a stream creates a cross-wired stream pair (see <see cref="InMemoryConnection"/>): the local
/// end is returned to the opener and the peer end is queued on the paired connection for its next
/// <see cref="AcceptStreamAsync(CancellationToken)"/>. There is no wire and no reflection; stream
/// routing is a pure <see cref="Channel{T}"/> hand-off and is fully trim-safe.
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("InMemoryMultiplexedConnection {Id,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class InMemoryMultiplexedConnection : MultiplexedConnection
{
    // Teardown and cancellation can be normal paths; resolve resource text once.
    private static readonly string _connectionClosedMessage = ErrorMessages.MultiplexedConnectionClosed;
    private static readonly string _connectionClosedWithErrorCodeMessage = ErrorMessages.MultiplexedConnectionClosedWithErrorCode;

    private readonly Channel<MultiplexedConnectionStream> _inboundStreams = Channel.CreateUnbounded<MultiplexedConnectionStream>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });

    private readonly ConnectionCapabilities _capabilities;
    private readonly ConnectionCapabilities _streamCapabilities;
    private readonly EndPoint? _localEndPoint;
    private readonly EndPoint? _remoteEndPoint;
    private readonly ConnectionId _id = ConnectionId.New();
    private readonly CancellationTokenSource _connectionClosedSource = new();
    // Both ends share this gate: opening a pair and choosing its first close are atomic together.
    private readonly Lock _gate;
    private readonly HashSet<InMemoryConnection.StreamEnd> _streams = new();
    private readonly Action<InMemoryConnection.StreamEnd> _onStreamClosed;

    private InMemoryMultiplexedConnection _peer = null!;
    private ConnectionState _state = ConnectionState.Open;
    private ConnectionResetException? _closeReason;

    private InMemoryMultiplexedConnection(
        ConnectionCapabilities capabilities,
        EndPoint? localEndPoint,
        EndPoint? remoteEndPoint,
        Lock gate)
    {
        _capabilities = capabilities;
        _streamCapabilities = capabilities with { IsMultiplexed = false };
        _localEndPoint = localEndPoint;
        _remoteEndPoint = remoteEndPoint;
        _gate = gate;
        _onStreamClosed = OnStreamClosed;
    }

    /// <inheritdoc />
    public override ConnectionId Id => _id;

    /// <inheritdoc />
    public override EndPoint? LocalEndPoint => _localEndPoint;

    /// <inheritdoc />
    public override EndPoint? RemoteEndPoint => _remoteEndPoint;

    /// <inheritdoc />
    public override ConnectionCapabilities Capabilities => _capabilities;

    /// <inheritdoc />
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

    /// <inheritdoc />
    public override CancellationToken ConnectionClosed => _connectionClosedSource.Token;

    /// <inheritdoc />
    public override async ValueTask<MultiplexedConnectionStream> AcceptStreamAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            MultiplexedConnectionStream stream = await _inboundStreams.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                if (_closeReason is not null)
                {
                    throw new OperationCanceledException(_connectionClosedMessage, cancellationToken);
                }
            }
            return stream;
        }
        catch (ChannelClosedException)
        {
            // No further inbound streams will arrive; the connection has closed.
            throw new OperationCanceledException(_connectionClosedMessage, cancellationToken);
        }
    }

    /// <inheritdoc />
    public override ValueTask<MultiplexedConnectionStream> OpenStreamAsync(ConnectionDirection direction = ConnectionDirection.Bidirectional, CancellationToken cancellationToken = default)
    {
        if (direction == ConnectionDirection.ReadOnly)
        {
            throw new ArgumentException(
                ErrorMessages.ReadOnlyStreamNotSupported,
                nameof(direction));
        }

        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (_closeReason is not null)
            {
                throw _closeReason;
            }

            // Track each end until it terminates, including after it leaves the accept queue.
            (InMemoryConnection.StreamEnd local, InMemoryConnection.StreamEnd remote) = InMemoryConnection.CreateStreamPair(
                _streamCapabilities,
                endPointA: _localEndPoint,
                endPointB: _remoteEndPoint,
                directionA: direction,
                onClosedA: _onStreamClosed,
                onClosedB: _peer._onStreamClosed);
            _streams.Add(local);
            _peer._streams.Add(remote);
            // Closing either writer also holds the pair gate, so this open cannot cross a close.
            _peer._inboundStreams.Writer.TryWrite(remote);
            return ValueTask.FromResult<MultiplexedConnectionStream>(local);
        }
    }

    /// <inheritdoc />
    public override void Abort(Exception? reason = null) => Abort(0, reason);

    /// <inheritdoc />
    public override void Abort(long errorCode, Exception? reason = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(errorCode);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(errorCode, (1L << 62) - 1);

        Close(errorCode, disposing: false);
    }

    /// <inheritdoc />
    public override ValueTask DisposeAsync()
    {
        Close(0, disposing: true);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Creates a cross-wired pair of in-memory multiplexed connections.
    /// </summary>
    /// <param name="capabilities">The capabilities both ends advertise (must report multiplexing).</param>
    /// <param name="endPointA">The local endpoint of the first end (and remote endpoint of the second).</param>
    /// <param name="endPointB">The local endpoint of the second end (and remote endpoint of the first).</param>
    /// <returns>The two cross-wired ends of the multiplexed connection.</returns>
    internal static (InMemoryMultiplexedConnection A, InMemoryMultiplexedConnection B) CreatePair(
        ConnectionCapabilities capabilities,
        EndPoint? endPointA,
        EndPoint? endPointB)
    {
        Lock gate = new();
        InMemoryMultiplexedConnection a = new(capabilities, endPointA, endPointB, gate);
        InMemoryMultiplexedConnection b = new(capabilities, endPointB, endPointA, gate);

        a._peer = b;
        b._peer = a;

        return (a, b);
    }

    private void Close(long errorCode, bool disposing)
    {
        InMemoryConnection.StreamEnd[] streams = [];
        lock (_gate)
        {
            if (_closeReason is null)
            {
                ConnectionResetException loss = new(
                    string.Format(CultureInfo.CurrentCulture, _connectionClosedWithErrorCodeMessage, errorCode), errorCode);
                _closeReason = loss;
                _peer._closeReason = loss;
                _state = disposing ? ConnectionState.Closed : ConnectionState.Aborted;
                _peer._state = ConnectionState.Aborted;

                streams = new InMemoryConnection.StreamEnd[_streams.Count + _peer._streams.Count];
                _streams.CopyTo(streams);
                _peer._streams.CopyTo(streams, _streams.Count);
                _streams.Clear();
                _peer._streams.Clear();
                // Mark every end before a completed accept can wake protocol teardown. This stores state
                // only; stream removal callbacks never run while holding a stream's mechanics gate.
                foreach (InMemoryConnection.StreamEnd stream in streams)
                {
                    stream.SetConnectionLoss(loss);
                }
                CompleteAcceptQueue();
                _peer.CompleteAcceptQueue();
            }
        }

        // A callback may read or dispose a stream. Pipe operations and callbacks run outside the pair
        // gate, after loss is published, to allow reentrant teardown without losing the close code.
        CancelConnectionClosed();
        _peer.CancelConnectionClosed();
        foreach (InMemoryConnection.StreamEnd stream in streams)
        {
            stream.FinishConnectionLoss();
        }
    }

    private void OnStreamClosed(InMemoryConnection.StreamEnd stream)
    {
        lock (_gate)
        {
            _streams.Remove(stream);
        }
    }

    private void CompleteAcceptQueue()
    {
        _inboundStreams.Writer.TryComplete();
        while (_inboundStreams.Reader.TryRead(out _))
        {
            // All live ends, queued or accepted, are in the close snapshot.
        }
    }

    private void CancelConnectionClosed()
    {
        try
        {
            _connectionClosedSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The token source was disposed concurrently with tear-down.
        }
        catch (AggregateException)
        {
            // A consumer callback must not prevent the other end and its streams from learning of loss.
        }
    }

    private sealed class DebugView
    {
        private readonly InMemoryMultiplexedConnection _target;

        public DebugView(InMemoryMultiplexedConnection value)
        {
            _target = value;
        }

        public ConnectionId Id => _target.Id;
        public EndPoint? LocalEndPoint => _target.LocalEndPoint;
        public EndPoint? RemoteEndPoint => _target.RemoteEndPoint;
        public ConnectionCapabilities Capabilities => _target.Capabilities;
        public ConnectionState State => _target.State;
        public CancellationToken ConnectionClosed => _target.ConnectionClosed;
        public global::Assimalign.Cohesion.Connections.IConnectionFeatureCollection? InnerFeatures => _target.InnerFeatures;
    }
}
