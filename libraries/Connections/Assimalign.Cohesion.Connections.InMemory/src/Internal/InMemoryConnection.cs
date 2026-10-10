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
/// flush — so a peer observes tear-down the next time it reads or writes.
/// </para>
/// <para>
/// The two ends of a multiplexed stream also hold each other, so an end that abandons the stream
/// signals the other end's <see cref="ConnectionClosed"/> at once, as a QUIC stream's peer abort does:
/// it aborts, or its holder completes <see cref="Output"/> with an error (the in-memory
/// <c>RESET_STREAM</c>) or <see cref="Input"/> with an error (the in-memory <c>STOP_SENDING</c>). A
/// clean completion is a half-close, not an abort, and signals nothing. The ends of a byte-stream pair
/// keep their closed tokens local.
/// </para>
/// <para>
/// An end of a multiplexed stream also implements <see cref="IMultiplexedStreamAbort"/>, the in-memory
/// <c>STOP_SENDING</c> and <c>RESET_STREAM</c> with an application error code: the other end's writes, or
/// its reads, fail with a <see cref="ConnectionResetException"/> that carries the code. An end of a
/// byte-stream pair does not implement it, as a single-stream transport cannot carry a code.
/// </para>
/// <para>
/// This type carries no diagnostics dependency and performs no reflection; it is a pure
/// <see cref="System.IO.Pipelines"/> composition and is fully trim-safe.
/// </para>
/// </remarks>
internal class InMemoryConnection : Connection
{
    // RFC 9000 §16 — the codes the QUIC driver accepts, so a test over this driver fails where QUIC would.
    private const long maxApplicationErrorCode = (1L << 62) - 1;

    // Non-pausing pipes: the in-memory transport favors deterministic, non-blocking byte movement
    // over back-pressure realism, so a synchronous prime write or a write-then-read on the same task
    // never blocks waiting for the peer. HTTP/2 and HTTP/3 exercise their own flow control above this.
    private static readonly PipeOptions _pipeOptionsInstance = new(
        pauseWriterThreshold: 0,
        resumeWriterThreshold: 0,
        useSynchronizationContext: false);

    private readonly PipeReader _input;
    private readonly PipeReader _inputView;
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
    // Set when this end abandoned its receiving direction with a code (AbortRead): reads fail from then on.
    private volatile bool _readAborted;
    private bool _isDisposed;

    // The other end of a multiplexed stream, which this end signals when it abandons the stream; null
    // for a byte-stream pair. Set once, before either end is handed out.
    private InMemoryConnection? _peer;

    private InMemoryConnection(
        PipeReader input,
        PipeWriter output,
        ConnectionDirection direction,
        ConnectionCapabilities capabilities,
        EndPoint? localEndPoint,
        EndPoint? remoteEndPoint,
        bool signalsPeer)
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

        // A multiplexed stream's holder that completes its input with an error stops the peer's
        // sending half, which the peer is told about; a byte-stream pair hands out the pipe reader itself.
        _inputView = signalsPeer ? new SignalingPipeReader(this, input) : input;
    }

    /// <inheritdoc />
    public override ConnectionId Id => _id;

    /// <inheritdoc />
    public override EndPoint? LocalEndPoint => _localEndPoint;

    /// <inheritdoc />
    public override EndPoint? RemoteEndPoint => _remoteEndPoint;

    /// <inheritdoc />
    public override PipeReader Input => _inputView;

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
        // receive side makes the peer's next flush observe completion. The peer learns of the abort,
        // and the other end of a multiplexed stream learns of it at once.
        CompleteOutput(abortReason);
        CompleteInput(abortReason);
        CancelConnectionClosed();
        SignalPeer();
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
    /// <param name="isStream">
    /// Whether the pair is a stream of a multiplexed connection, whose ends signal each other's
    /// <see cref="ConnectionClosed"/> when one abandons the stream.
    /// </param>
    /// <returns>The two cross-wired ends of the connection.</returns>
    internal static (InMemoryConnection A, InMemoryConnection B) CreatePair(
        ConnectionCapabilities capabilities,
        EndPoint? endPointA,
        EndPoint? endPointB,
        ConnectionDirection directionA = ConnectionDirection.Bidirectional,
        bool isStream = false)
    {
        // aToB carries A.Output -> B.Input; bToA carries B.Output -> A.Input.
        Pipe aToB = new(_pipeOptionsInstance);
        Pipe bToA = new(_pipeOptionsInstance);

        ConnectionDirection directionB = directionA switch
        {
            ConnectionDirection.WriteOnly => ConnectionDirection.ReadOnly,
            ConnectionDirection.ReadOnly => ConnectionDirection.WriteOnly,
            _ => ConnectionDirection.Bidirectional
        };

        InMemoryConnection a = isStream
            ? new StreamEnd(bToA.Reader, aToB.Writer, directionA, capabilities, endPointA, endPointB)
            : new InMemoryConnection(bToA.Reader, aToB.Writer, directionA, capabilities, endPointA, endPointB, signalsPeer: false);
        InMemoryConnection b = isStream
            ? new StreamEnd(aToB.Reader, bToA.Writer, directionB, capabilities, endPointB, endPointA)
            : new InMemoryConnection(aToB.Reader, bToA.Writer, directionB, capabilities, endPointB, endPointA, signalsPeer: false);

        if (isStream)
        {
            a._peer = b;
            b._peer = a;
        }

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

    // This end abandoned the stream: the other end of a multiplexed stream observes it on its
    // ConnectionClosed, as a QUIC stream's peer abort does. Its state is left alone: the stream is the
    // other end's to close. Registrations run on the calling thread, as the end's own abort runs them.
    private void SignalPeer()
    {
        _peer?.CancelConnectionClosed();
    }

    // The in-memory STOP_SENDING: completing the receive pipe with the reset makes the other end's next
    // flush throw it. A read waiting on the pipe is woken first, because completing a pipe reader leaves a
    // pending read waiting for the writer; the delegating reader then fails it.
    private void AbortInput(long errorCode)
    {
        ValidateErrorCode(errorCode);

        lock (_gate)
        {
            if (_inputCompleted || _direction == ConnectionDirection.WriteOnly)
            {
                return;
            }

            _inputCompleted = true;
            _readAborted = true;
        }

        _input.CancelPendingRead();

        try
        {
            _input.Complete(new ConnectionResetException(
                $"The peer stopped reading the stream with application error code 0x{errorCode:x}.",
                errorCode));
        }
        catch (InvalidOperationException)
        {
            // The underlying reader was already completed; nothing to do.
        }

        SignalPeer();
    }

    // The in-memory RESET_STREAM: completing the send pipe with the reset makes the other end's reads throw
    // it, a pending read included.
    private void AbortOutput(long errorCode)
    {
        ValidateErrorCode(errorCode);

        lock (_gate)
        {
            if (_outputCompleted || _direction == ConnectionDirection.ReadOnly)
            {
                return;
            }

            _outputCompleted = true;
        }

        try
        {
            _outputInner.Complete(new ConnectionResetException(
                $"The peer reset the stream with application error code 0x{errorCode:x}.",
                errorCode));
        }
        catch (InvalidOperationException)
        {
            // The underlying writer was already completed; nothing to do.
        }

        SignalPeer();
    }

    private static void ValidateErrorCode(long errorCode)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(errorCode);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(errorCode, maxApplicationErrorCode);
    }

    private static ConnectionAbortedException ReadAborted()
        => new("Reading was aborted on this end of the stream.");

    /// <summary>
    /// One end of a stream of an in-memory multiplexed connection, which can abandon either direction with an
    /// application error code.
    /// </summary>
    private sealed class StreamEnd : InMemoryConnection, IMultiplexedStreamAbort
    {
        public StreamEnd(
            PipeReader input,
            PipeWriter output,
            ConnectionDirection direction,
            ConnectionCapabilities capabilities,
            EndPoint? localEndPoint,
            EndPoint? remoteEndPoint)
            : base(input, output, direction, capabilities, localEndPoint, remoteEndPoint, signalsPeer: true)
        {
        }

        /// <inheritdoc />
        public void AbortRead(long errorCode) => AbortInput(errorCode);

        /// <inheritdoc />
        public void AbortWrite(long errorCode) => AbortOutput(errorCode);
    }

    /// <summary>
    /// A delegating <see cref="PipeWriter"/> that transitions the owning connection to
    /// <see cref="ConnectionState.Closing"/> when the holder completes the send side, mirroring a real
    /// transport's send loop draining its backlog before the connection closes. Completing it with an
    /// error resets the sending half, which the other end of a multiplexed stream is told about.
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

            if (exception is not null)
            {
                _connection.SignalPeer();
            }
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

            if (exception is not null)
            {
                _connection.SignalPeer();
            }
        }
    }

    /// <summary>
    /// A delegating <see cref="PipeReader"/> for an end of a multiplexed stream: completing it with an
    /// error stops the peer's sending half, which the other end is told about. Once the end abandons its
    /// receiving direction (<see cref="IMultiplexedStreamAbort.AbortRead(long)"/>), a read in flight and every
    /// later read fail with <see cref="ConnectionAbortedException"/>, as a QUIC stream's reads fail after
    /// its own <c>STOP_SENDING</c>.
    /// </summary>
    private sealed class SignalingPipeReader : PipeReader
    {
        private readonly InMemoryConnection _connection;
        private readonly PipeReader _inner;

        public SignalingPipeReader(InMemoryConnection connection, PipeReader inner)
        {
            _connection = connection;
            _inner = inner;
        }

        public override void AdvanceTo(SequencePosition consumed)
        {
            if (!_connection._readAborted)
            {
                _inner.AdvanceTo(consumed);
            }
        }

        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
        {
            if (!_connection._readAborted)
            {
                _inner.AdvanceTo(consumed, examined);
            }
        }

        public override void CancelPendingRead()
        {
            if (!_connection._readAborted)
            {
                _inner.CancelPendingRead();
            }
        }

        public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            if (_connection._readAborted)
            {
                return ValueTask.FromException<ReadResult>(ReadAborted());
            }

            ValueTask<ReadResult> read = _inner.ReadAsync(cancellationToken);

            return read.IsCompletedSuccessfully && !_connection._readAborted
                ? read
                : AwaitReadAsync(read);
        }

        public override bool TryRead(out ReadResult result)
        {
            if (_connection._readAborted)
            {
                throw ReadAborted();
            }

            return _inner.TryRead(out result);
        }

        public override void Complete(Exception? exception = null)
        {
            _connection.CompleteInput(exception);

            if (exception is not null)
            {
                _connection.SignalPeer();
            }
        }

        // A read the abort overtook fails, whatever the pipe handed it: the canceled result that woke it, or
        // the InvalidOperationException of a pipe reader completed underneath it.
        private async ValueTask<ReadResult> AwaitReadAsync(ValueTask<ReadResult> read)
        {
            ReadResult result;

            try
            {
                result = await read.ConfigureAwait(false);
            }
            catch (InvalidOperationException) when (_connection._readAborted)
            {
                throw ReadAborted();
            }

            if (_connection._readAborted)
            {
                throw ReadAborted();
            }

            return result;
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
