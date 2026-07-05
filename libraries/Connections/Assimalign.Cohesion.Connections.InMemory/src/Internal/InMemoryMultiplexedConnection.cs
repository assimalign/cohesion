using System;
using System.Net;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Connections.InMemory.Internal;

/// <summary>
/// One end of a cross-wired, in-memory <see cref="MultiplexedConnection"/>. Streams opened on one end
/// are delivered to the other end's accept queue; each stream is itself an in-memory
/// <see cref="Connection"/>, so h2/h3-shaped tests can open and accept multiple concurrent streams.
/// </summary>
/// <remarks>
/// Opening a stream creates a cross-wired stream pair (see <see cref="InMemoryConnection"/>): the local
/// end is returned to the opener and the peer end is queued on the paired connection for its next
/// <see cref="AcceptStreamAsync(CancellationToken)"/>. There is no wire and no reflection; stream
/// routing is a pure <see cref="Channel{T}"/> hand-off and is fully trim-safe.
/// </remarks>
internal sealed class InMemoryMultiplexedConnection : MultiplexedConnection
{
    private readonly Channel<Connection> _inboundStreams = Channel.CreateUnbounded<Connection>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });

    private readonly ConnectionCapabilities _capabilities;
    private readonly ConnectionCapabilities _streamCapabilities;
    private readonly EndPoint? _localEndPoint;
    private readonly EndPoint? _remoteEndPoint;
    private readonly ConnectionId _id = ConnectionId.New();
    private readonly CancellationTokenSource _connectionClosedSource = new();
    private readonly Lock _gate = new();

    private InMemoryMultiplexedConnection _peer = null!;
    private ConnectionState _state = ConnectionState.Open;
    private bool _isDisposed;

    private InMemoryMultiplexedConnection(
        ConnectionCapabilities capabilities,
        EndPoint? localEndPoint,
        EndPoint? remoteEndPoint)
    {
        _capabilities = capabilities;
        _streamCapabilities = capabilities with { IsMultiplexed = false };
        _localEndPoint = localEndPoint;
        _remoteEndPoint = remoteEndPoint;
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
    public override async ValueTask<Connection> AcceptStreamAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _inboundStreams.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            // No further inbound streams will arrive; the connection has closed.
            throw new OperationCanceledException("The in-memory multiplexed connection has been closed.", cancellationToken);
        }
    }

    /// <inheritdoc />
    public override ValueTask<Connection> OpenStreamAsync(ConnectionDirection direction = ConnectionDirection.Bidirectional, CancellationToken cancellationToken = default)
    {
        if (direction == ConnectionDirection.ReadOnly)
        {
            throw new ArgumentException(
                "A peer cannot open a read-only stream; only the remote side would be able to write to it.",
                nameof(direction));
        }

        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (_state is ConnectionState.Aborted or ConnectionState.Closed)
            {
                throw new ConnectionAbortedException("Cannot open a stream on a closed multiplexed connection.");
            }
        }

        // The local end takes the requested direction; the peer end takes the mirror direction.
        (InMemoryConnection local, InMemoryConnection remote) = InMemoryConnection.CreatePair(
            _streamCapabilities,
            endPointA: _localEndPoint,
            endPointB: _remoteEndPoint,
            directionA: direction);

        if (!_peer._inboundStreams.Writer.TryWrite(remote))
        {
            _ = local.DisposeAsync();
            _ = remote.DisposeAsync();

            throw new ConnectionAbortedException("The peer multiplexed connection has been closed.");
        }

        return ValueTask.FromResult<Connection>(local);
    }

    /// <inheritdoc />
    public override void Abort(Exception? reason = null)
    {
        lock (_gate)
        {
            if (_state is ConnectionState.Aborted or ConnectionState.Closed)
            {
                return;
            }

            _state = ConnectionState.Aborted;
        }

        TearDownStreams();
        CancelConnectionClosed();
    }

    /// <inheritdoc />
    public override ValueTask DisposeAsync()
    {
        bool wasAborted;

        lock (_gate)
        {
            if (_isDisposed)
            {
                return ValueTask.CompletedTask;
            }

            _isDisposed = true;
            wasAborted = _state == ConnectionState.Aborted;
        }

        TearDownStreams();

        if (!wasAborted)
        {
            lock (_gate)
            {
                _state = ConnectionState.Closed;
            }
        }

        CancelConnectionClosed();

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
        InMemoryMultiplexedConnection a = new(capabilities, endPointA, endPointB);
        InMemoryMultiplexedConnection b = new(capabilities, endPointB, endPointA);

        a._peer = b;
        b._peer = a;

        return (a, b);
    }

    private void TearDownStreams()
    {
        _inboundStreams.Writer.TryComplete();

        while (_inboundStreams.Reader.TryRead(out Connection? stream))
        {
            _ = stream.DisposeAsync();
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
    }
}
