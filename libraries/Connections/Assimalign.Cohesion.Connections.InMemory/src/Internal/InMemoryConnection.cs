using System;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Connections.InMemory.Internal;

/// <summary>
/// One end of a cross-wired, in-memory <see cref="Connection"/>. The two ends of a pair share two
/// <see cref="Pipe"/> instances so that bytes written to one end's <see cref="Output"/> arrive on the
/// other end's <see cref="Input"/>, exactly like the two ends of a real transport connection.
/// </summary>
/// <remarks>
/// <para>
/// There is no wire and therefore no pump loop: the consumer's <see cref="Output"/> writer and the
/// peer's <see cref="Input"/> reader are the two ends of the same pipe, so a flush on one is directly
/// observable on the other. Close and abort propagate through pipe completion — completing this end's
/// output completes the peer's input, and completing this end's input completes the peer's output
/// flush — so a peer observes tear-down the next time it reads or writes, without any peer reference.
/// </para>
/// <para>
/// This type carries no diagnostics dependency and performs no reflection; it is a pure
/// <see cref="System.IO.Pipelines"/> composition and is fully trim-safe.
/// </para>
/// </remarks>
internal sealed class InMemoryConnection : Connection
{
    // Non-pausing pipes: the in-memory transport favors deterministic, non-blocking byte movement
    // over back-pressure realism, so a synchronous prime write or a write-then-read on the same task
    // never blocks waiting for the peer. HTTP/2 and HTTP/3 exercise their own flow control above this.
    private static readonly PipeOptions PipeOptionsInstance = new(
        pauseWriterThreshold: 0,
        resumeWriterThreshold: 0,
        useSynchronizationContext: false);

    private readonly PipeReader _input;
    private readonly PipeWriter _outputInner;
    private readonly PipeWriter _output;
    private readonly ConnectionCapabilities _capabilities;
    private readonly ConnectionDirection _direction;
    private readonly EndPoint? _localEndPoint;
    private readonly EndPoint? _remoteEndPoint;
    private readonly ConnectionId _id = ConnectionId.New();
    private readonly CancellationTokenSource _connectionClosedSource = new();
    private readonly Lock _gate = new();

    private ConnectionState _state = ConnectionState.Open;
    private bool _outputCompleted;
    private bool _inputCompleted;
    private bool _isDisposed;

    private InMemoryConnection(
        PipeReader input,
        PipeWriter output,
        ConnectionDirection direction,
        ConnectionCapabilities capabilities,
        EndPoint? localEndPoint,
        EndPoint? remoteEndPoint)
    {
        _input = input;
        _outputInner = output;
        _direction = direction;
        _capabilities = capabilities;
        _localEndPoint = localEndPoint;
        _remoteEndPoint = remoteEndPoint;

        // A read-only stream must reject writes; every other direction exposes the send pipe through a
        // wrapper that transitions the connection to Closing when the holder completes it (a real send
        // loop drains and then closes).
        _output = direction == ConnectionDirection.ReadOnly
            ? ThrowingPipeWriter.Instance
            : new SignalingPipeWriter(this, output);
    }

    /// <inheritdoc />
    public override ConnectionId Id => _id;

    /// <inheritdoc />
    public override EndPoint? LocalEndPoint => _localEndPoint;

    /// <inheritdoc />
    public override EndPoint? RemoteEndPoint => _remoteEndPoint;

    /// <inheritdoc />
    public override PipeReader Input => _input;

    /// <inheritdoc />
    public override PipeWriter Output => _output;

    /// <inheritdoc />
    public override ConnectionDirection Direction => _direction;

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

        Exception abortReason = reason ?? new ConnectionAbortedException();

        // Completing the send side with the reason makes the peer's read throw it; completing the
        // receive side makes the peer's next flush observe completion. The peer learns of the abort.
        CompleteOutput(abortReason);
        CompleteInput(abortReason);
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

        // A graceful dispose completes both halves without an error so the peer observes end-of-stream.
        CompleteOutput(null);
        CompleteInput(null);

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
    /// Creates a cross-wired pair of in-memory connections.
    /// </summary>
    /// <param name="capabilities">The capabilities both ends advertise.</param>
    /// <param name="endPointA">The local endpoint of the first end (and remote endpoint of the second).</param>
    /// <param name="endPointB">The local endpoint of the second end (and remote endpoint of the first).</param>
    /// <param name="directionA">
    /// The direction of the first end. The second end takes the mirror direction:
    /// <see cref="ConnectionDirection.Bidirectional"/> mirrors to itself, while
    /// <see cref="ConnectionDirection.WriteOnly"/> and <see cref="ConnectionDirection.ReadOnly"/> mirror each other.
    /// </param>
    /// <returns>The two cross-wired ends of the connection.</returns>
    internal static (InMemoryConnection A, InMemoryConnection B) CreatePair(
        ConnectionCapabilities capabilities,
        EndPoint? endPointA,
        EndPoint? endPointB,
        ConnectionDirection directionA = ConnectionDirection.Bidirectional)
    {
        // aToB carries A.Output -> B.Input; bToA carries B.Output -> A.Input.
        Pipe aToB = new(PipeOptionsInstance);
        Pipe bToA = new(PipeOptionsInstance);

        ConnectionDirection directionB = directionA switch
        {
            ConnectionDirection.WriteOnly => ConnectionDirection.ReadOnly,
            ConnectionDirection.ReadOnly => ConnectionDirection.WriteOnly,
            _ => ConnectionDirection.Bidirectional
        };

        InMemoryConnection a = new(bToA.Reader, aToB.Writer, directionA, capabilities, endPointA, endPointB);
        InMemoryConnection b = new(aToB.Reader, bToA.Writer, directionB, capabilities, endPointB, endPointA);

        // A unidirectional stream leaves one pipe degenerate: the write-only end never reads and the
        // read-only end never writes. Pre-complete the unused receive half so the write-only end sees
        // end-of-stream on its input immediately rather than blocking forever.
        if (directionA == ConnectionDirection.WriteOnly)
        {
            a.CompleteInput(null);
        }
        else if (directionA == ConnectionDirection.ReadOnly)
        {
            b.CompleteInput(null);
        }

        return (a, b);
    }

    private void CompleteOutput(Exception? exception)
    {
        lock (_gate)
        {
            if (_outputCompleted)
            {
                return;
            }

            _outputCompleted = true;
        }

        try
        {
            _outputInner.Complete(exception);
        }
        catch (InvalidOperationException)
        {
            // The underlying writer was already completed (for example by the holder); nothing to do.
        }
    }

    private void CompleteInput(Exception? exception)
    {
        lock (_gate)
        {
            if (_inputCompleted)
            {
                return;
            }

            _inputCompleted = true;
        }

        try
        {
            _input.Complete(exception);
        }
        catch (InvalidOperationException)
        {
            // The underlying reader was already completed; nothing to do.
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

    private void OnOutputCompletedByHolder()
    {
        lock (_gate)
        {
            if (_state == ConnectionState.Open)
            {
                _state = ConnectionState.Closing;
            }

            _outputCompleted = true;
        }
    }

    /// <summary>
    /// A delegating <see cref="PipeWriter"/> that transitions the owning connection to
    /// <see cref="ConnectionState.Closing"/> when the holder completes the send side, mirroring a real
    /// transport's send loop draining its backlog before the connection closes.
    /// </summary>
    private sealed class SignalingPipeWriter : PipeWriter
    {
        private readonly InMemoryConnection _connection;
        private readonly PipeWriter _inner;

        public SignalingPipeWriter(InMemoryConnection connection, PipeWriter inner)
        {
            _connection = connection;
            _inner = inner;
        }

        public override void Advance(int bytes) => _inner.Advance(bytes);

        public override Memory<byte> GetMemory(int sizeHint = 0) => _inner.GetMemory(sizeHint);

        public override Span<byte> GetSpan(int sizeHint = 0) => _inner.GetSpan(sizeHint);

        public override void CancelPendingFlush() => _inner.CancelPendingFlush();

        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
            => _inner.FlushAsync(cancellationToken);

        public override void Complete(Exception? exception = null)
        {
            try
            {
                _inner.Complete(exception);
            }
            catch (InvalidOperationException)
            {
                // Already completed.
            }

            _connection.OnOutputCompletedByHolder();
        }

        public override async ValueTask CompleteAsync(Exception? exception = null)
        {
            try
            {
                await _inner.CompleteAsync(exception).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // Already completed.
            }

            _connection.OnOutputCompletedByHolder();
        }
    }

    /// <summary>
    /// A <see cref="PipeWriter"/> whose write operations throw, used for the send side of a read-only
    /// (inbound unidirectional) stream, where writing is not permitted.
    /// </summary>
    private sealed class ThrowingPipeWriter : PipeWriter
    {
        public static ThrowingPipeWriter Instance { get; } = new();

        private static InvalidOperationException Fail()
            => new("Cannot write to the output of a read-only connection.");

        public override void Advance(int bytes) => throw Fail();

        public override Memory<byte> GetMemory(int sizeHint = 0) => throw Fail();

        public override Span<byte> GetSpan(int sizeHint = 0) => throw Fail();

        public override void CancelPendingFlush()
        {
            // No pending flush is possible on a writer that never accepts data.
        }

        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
            => throw Fail();

        public override void Complete(Exception? exception = null)
        {
            // Completing the unusable send side is a harmless no-op.
        }
    }
}
