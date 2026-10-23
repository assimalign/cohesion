using System;
using System.Globalization;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Connections.InMemory.Internal;

/// <summary>
/// One end of a cross-wired, in-memory <see cref="DuplexConnection"/>. The two ends of a pair share two
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
/// The two ends hold each other, so an end that closes or abandons the connection
/// signals the other end's <see cref="ConnectionClosed"/> at once, as a QUIC stream's peer abort does:
/// it aborts, or its holder completes <see cref="Output"/> with an error (the in-memory
/// <c>RESET_STREAM</c>) or <see cref="Input"/> with an error (the in-memory <c>STOP_SENDING</c>). A
/// clean completion of one direction is a half-close and leaves the other direction usable.
/// Disposal signals both ends without requiring the peer to read or write.
/// </para>
/// <para>
/// The composing end of a multiplexed stream implements <see cref="IMultiplexedStreamAbort"/>, the in-memory
/// <c>STOP_SENDING</c> and <c>RESET_STREAM</c> with an application error code: the other end's writes, or
/// its reads, fail with a <see cref="ConnectionResetException"/> that carries the code. An end of a
/// byte-stream pair does not implement it, as a single-stream transport cannot carry a code.
/// </para>
/// <para>
/// A multiplexed parent closing publishes a coded connection loss on every live stream end before
/// waking cancellation observers or pipe operations. The pipe views retain that loss after disposal,
/// so a pending read or later write reports the connection code even when observer cleanup completed
/// the underlying pipe. Stream ends leave the parent's live set once their lifecycle or both usable
/// holder halves have ended; removal callbacks never run under the mechanics gate.
/// </para>
/// <para>
/// This type carries no diagnostics dependency and performs no reflection; it is a pure
/// <see cref="System.IO.Pipelines"/> composition and is fully trim-safe.
/// </para>
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("InMemoryConnection {Id,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class InMemoryConnection : DuplexConnection
{
    // Teardown and cancellation can be normal paths; resolve resource text once.
    private static readonly string _peerReadingStoppedMessage = ErrorMessages.PeerReadingStopped;
    private static readonly string _streamResetByPeerMessage = ErrorMessages.StreamResetByPeer;
    private static readonly string _streamReadingAbortedMessage = ErrorMessages.StreamReadingAborted;

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
    private bool _outputCompletedCleanly;
    private bool _inputCompleted;
    // A local receive failure or whole-stream abort cannot become a clean end when a later FIN arrives.
    private volatile bool _inputFailed;
    // Set when this end abandoned its receiving direction with a code (AbortRead): reads fail from then on.
    private volatile bool _readAborted;
    // Published before either parent connection token fires, so cancellation-driven cleanup cannot
    // replace the connection loss with a local stream abort or an already-completed pipe exception.
    private volatile ConnectionResetException? _connectionLoss;
    private bool _isDisposed;
    private StreamEnd? _streamEnd;

    // The other end, signaled on abort or disposal without requiring a peer read or write.
    // Set once, before either end is handed out.
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

    internal bool IsInputCompleted
    {
        get
        {
            if (_connectionLoss is not null || _readAborted || _inputFailed || _peer is null)
            {
                return false;
            }

            lock (_peer._gate)
            {
                return _peer._outputCompletedCleanly;
            }
        }
    }

    /// <inheritdoc />
    public override void Abort(Exception? reason = null)
    {
        lock (_gate)
        {
            if (_state is not (ConnectionState.Aborted or ConnectionState.Closed))
            {
                _state = ConnectionState.Aborted;
                _inputFailed = true;
                Exception abortReason = reason ?? new ConnectionAbortedException();
                CompleteOutput(abortReason);
                CompleteInput(abortReason);
            }
        }

        // Completing the send side with the reason makes the peer's read throw it; completing the
        // receive side makes the peer's next flush observe completion. The peer learns of the abort,
        // and the other end of a multiplexed stream learns of it at once.
        CancelConnectionClosed();
        SignalPeer();
        _streamEnd?.NotifyClosedIfEnded();
    }

    /// <inheritdoc />
    public override ValueTask DisposeAsync()
    {
        bool connectionLost;
        bool alreadyDisposed;

        lock (_gate)
        {
            alreadyDisposed = _isDisposed;
            _isDisposed = true;
            connectionLost = _connectionLoss is not null;
            if (_state != ConnectionState.Aborted)
            {
                _state = ConnectionState.Closed;
            }

            // Complete under the lifecycle gate so a later abort cannot choose either direction's end.
            if (!connectionLost && !alreadyDisposed)
            {
                CompleteOutput(null);
                CompleteInput(null);
            }
        }

        if (connectionLost)
        {
            _streamEnd!.FinishConnectionLoss();
        }
        CancelConnectionClosed();
        SignalPeer();
        _streamEnd?.NotifyClosedIfEnded();

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

        InMemoryConnection a = new(bToA.Reader, aToB.Writer, directionA, capabilities, endPointA, endPointB, signalsPeer: isStream);
        InMemoryConnection b = new(aToB.Reader, bToA.Writer, directionB, capabilities, endPointB, endPointA, signalsPeer: isStream);

        a._peer = b;
        b._peer = a;

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

    internal static (StreamEnd A, StreamEnd B) CreateStreamPair(
        ConnectionCapabilities capabilities,
        EndPoint? endPointA,
        EndPoint? endPointB,
        ConnectionDirection directionA,
        Action<StreamEnd> onClosedA,
        Action<StreamEnd> onClosedB)
    {
        (InMemoryConnection a, InMemoryConnection b) = CreatePair(
            capabilities, endPointA, endPointB, directionA, isStream: true);
        return (new StreamEnd(a, onClosedA), new StreamEnd(b, onClosedB));
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
            exception = _connectionLoss ?? exception;
            try
            {
                _outputInner.Complete(exception);
                _outputCompletedCleanly = exception is null;
            }
            catch (InvalidOperationException)
            {
                // The underlying writer was already completed; nothing to do.
            }
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
            exception = _connectionLoss ?? exception;
            _inputFailed |= exception is not null;
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
        catch (AggregateException)
        {
            // A registration cannot prevent peer notification or the remaining stream teardown.
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
    private void AbortInput(long errorCode, bool signalPeer = true)
    {
        ValidateErrorCode(errorCode);

        lock (_gate)
        {
            if (_connectionLoss is not null || _inputCompleted || _direction == ConnectionDirection.WriteOnly)
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
                string.Format(CultureInfo.CurrentCulture, _peerReadingStoppedMessage, errorCode),
                errorCode));
        }
        catch (InvalidOperationException)
        {
            // The underlying reader was already completed; nothing to do.
        }

        if (signalPeer)
        {
            SignalPeer();
        }
    }

    // The in-memory RESET_STREAM: completing the send pipe with the reset makes the other end's reads throw
    // it, a pending read included.
    private void AbortOutput(long errorCode, bool signalPeer = true)
    {
        ValidateErrorCode(errorCode);

        lock (_gate)
        {
            if (_connectionLoss is not null || _outputCompleted || _direction == ConnectionDirection.ReadOnly)
            {
                return;
            }

            _outputCompleted = true;
        }

        try
        {
            _outputInner.Complete(new ConnectionResetException(
                string.Format(CultureInfo.CurrentCulture, _streamResetByPeerMessage, errorCode),
                errorCode));
        }
        catch (InvalidOperationException)
        {
            // The underlying writer was already completed; nothing to do.
        }

        if (signalPeer)
        {
            SignalPeer();
        }
    }

    private static void ValidateErrorCode(long errorCode)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(errorCode);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(errorCode, maxApplicationErrorCode);
    }

    private static ConnectionAbortedException ReadAborted()
        => new(_streamReadingAbortedMessage);

    private void CheckConnectionLoss()
    {
        if (_connectionLoss is { } loss)
        {
            throw loss;
        }
    }

    /// <summary>
    /// One end of a stream of an in-memory multiplexed connection, which can abandon either direction with an
    /// application error code.
    /// </summary>
    [global::System.Diagnostics.DebuggerDisplay("StreamEnd {Id,nq}")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
    internal sealed class StreamEnd : MultiplexedConnectionStream, IMultiplexedStreamAbort
    {
        private readonly InMemoryConnection _inner;
        private readonly Action<StreamEnd> _onClosed;
        private int _closedReported;
        private int _connectionLossFinished;

        public StreamEnd(InMemoryConnection inner, Action<StreamEnd> onClosed)
        {
            _inner = inner;
            _onClosed = onClosed;
            inner._streamEnd = this;
        }

        public override ConnectionId Id => _inner.Id;
        public override EndPoint? LocalEndPoint => _inner.LocalEndPoint;
        public override EndPoint? RemoteEndPoint => _inner.RemoteEndPoint;
        public override PipeReader Input => _inner.Input;
        public override PipeWriter Output => _inner.Output;
        public override ConnectionDirection Direction => _inner.Direction;
        public override ConnectionCapabilities Capabilities => _inner.Capabilities;
        public override ConnectionState State => _inner.State;
        public override CancellationToken ConnectionClosed => _inner.ConnectionClosed;
        public override bool IsInputCompleted => _inner.IsInputCompleted;

        string IConnectionFeature.Name => IMultiplexedStreamAbort.FeatureName;

        protected override void ConfigureFeatures(IConnectionFeatureCollection features)
        {
            features.Set(this);
        }

        public override void Abort(Exception? reason = null) => _inner.Abort(reason);

        public override void Abort(long errorCode, Exception? reason = null)
        {
            ValidateErrorCode(errorCode);
            lock (_inner._gate)
            {
                if (_inner._state is not (ConnectionState.Aborted or ConnectionState.Closed))
                {
                    // Publish both resets before notifying the peer or ending the lifecycle.
                    _inner.AbortInput(errorCode, signalPeer: false);
                    _inner.AbortOutput(errorCode, signalPeer: false);
                    _inner._state = ConnectionState.Aborted;
                    _inner._inputFailed = true;
                }
            }

            _inner.CancelConnectionClosed();
            _inner.SignalPeer();
            NotifyClosedIfEnded();
        }

        public override ValueTask DisposeAsync() => _inner.DisposeAsync();

        public void AbortRead(long errorCode)
        {
            _inner.AbortInput(errorCode);
            NotifyClosedIfEnded();
        }

        public void AbortWrite(long errorCode)
        {
            _inner.AbortOutput(errorCode);
            NotifyClosedIfEnded();
        }

        // The parent first marks every live end, then signals both connection tokens. No callbacks,
        // pipe completions or wakes occur here: all observers must see the loss before cleanup starts.
        internal void SetConnectionLoss(ConnectionResetException reason)
        {
            lock (_inner._gate)
            {
                if (HasEnded())
                {
                    return;
                }

                _inner._connectionLoss = reason;
                _inner._state = ConnectionState.Aborted;
            }
        }

        // A parent-token callback may dispose this end before the parent's finishing pass reaches it.
        // Both routes use the same loss and one-shot cleanup; wrappers retain the loss after disposal.
        internal void FinishConnectionLoss()
        {
            if (_inner._connectionLoss is not { } loss
                || Interlocked.Exchange(ref _connectionLossFinished, 1) != 0)
            {
                return;
            }

            try
            {
                _inner._input.CancelPendingRead();
            }
            catch (InvalidOperationException)
            {
                // A consumer may already have completed the receive pipe during cancellation.
            }

            _inner.CompleteOutput(loss);
            _inner.CompleteInput(loss);
            _inner.CancelConnectionClosed();
            NotifyClosedIfEnded();
        }

        internal void NotifyClosedIfEnded()
        {
            bool ended;
            bool outputCompleted;
            lock (_inner._gate)
            {
                ended = HasEnded();
                outputCompleted = _inner._outputCompletedCleanly;
            }

            // Each output is the peer's input. Two FINs close both native directions even when
            // holders still own buffered reads. Never acquire both mechanics gates together.
            if (!ended && outputCompleted && Direction == ConnectionDirection.Bidirectional)
            {
                bool peerOutputCompleted;
                lock (_inner._peer!._gate)
                {
                    peerOutputCompleted = _inner._peer._outputCompletedCleanly;
                }
                if (peerOutputCompleted)
                {
                    _inner.CancelConnectionClosed();
                    _inner.SignalPeer();
                }
            }

            // The parent owns a different gate. Never call back while holding the mechanics gate.
            if (ended)
            {
                _inner.CancelConnectionClosed();
                _inner.SignalPeer();
                if (Interlocked.Exchange(ref _closedReported, 1) == 0)
                {
                    _onClosed(this);
                }
            }
        }

        private bool HasEnded()
            => _inner._state is ConnectionState.Aborted or ConnectionState.Closed
                || ((_inner._inputCompleted || Direction == ConnectionDirection.WriteOnly)
                    && (_inner._outputCompleted || Direction == ConnectionDirection.ReadOnly));

        private sealed class DebugView
        {
            private readonly StreamEnd _target;

            public DebugView(StreamEnd value)
            {
                _target = value;
            }

            public ConnectionId Id => _target.Id;
            public EndPoint? LocalEndPoint => _target.LocalEndPoint;
            public EndPoint? RemoteEndPoint => _target.RemoteEndPoint;
            public PipeReader Input => _target.Input;
            public PipeWriter Output => _target.Output;
            public ConnectionDirection Direction => _target.Direction;
            public ConnectionCapabilities Capabilities => _target.Capabilities;
            public ConnectionState State => _target.State;
            public CancellationToken ConnectionClosed => _target.ConnectionClosed;
            public bool IsInputCompleted => _target.IsInputCompleted;
            public global::Assimalign.Cohesion.Connections.IConnectionFeatureCollection? InnerFeatures => _target.InnerFeatures;
        }
    }

    /// <summary>
    /// A delegating <see cref="PipeWriter"/> that transitions the owning connection to
    /// <see cref="ConnectionState.Closing"/> when the holder completes the send side, mirroring a real
    /// transport's send loop draining its backlog before the connection closes. Completing it with an
    /// error resets the sending half, which the other end of a multiplexed stream is told about.
    /// </summary>
    [global::System.Diagnostics.DebuggerDisplay("SignalingPipeWriter")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
    private sealed class SignalingPipeWriter : PipeWriter
    {
        private readonly InMemoryConnection _connection;
        private readonly PipeWriter _inner;

        public SignalingPipeWriter(InMemoryConnection connection, PipeWriter inner)
        {
            _connection = connection;
            _inner = inner;
        }

        public override void Advance(int bytes)
        {
            _connection.CheckConnectionLoss();
            try
            {
                _inner.Advance(bytes);
            }
            catch (InvalidOperationException) when (_connection._connectionLoss is not null)
            {
                throw _connection._connectionLoss!;
            }
        }

        public override Memory<byte> GetMemory(int sizeHint = 0)
        {
            _connection.CheckConnectionLoss();
            try
            {
                return _inner.GetMemory(sizeHint);
            }
            catch (InvalidOperationException) when (_connection._connectionLoss is not null)
            {
                throw _connection._connectionLoss!;
            }
        }

        public override Span<byte> GetSpan(int sizeHint = 0)
        {
            _connection.CheckConnectionLoss();
            try
            {
                return _inner.GetSpan(sizeHint);
            }
            catch (InvalidOperationException) when (_connection._connectionLoss is not null)
            {
                throw _connection._connectionLoss!;
            }
        }

        public override void CancelPendingFlush()
        {
            if (_connection._connectionLoss is null)
            {
                try
                {
                    _inner.CancelPendingFlush();
                }
                catch (InvalidOperationException) when (_connection._connectionLoss is not null)
                {
                    // Connection teardown already woke and completed the pending operation.
                }
            }
        }

        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            _connection.CheckConnectionLoss();
            try
            {
                ValueTask<FlushResult> flush = _inner.FlushAsync(cancellationToken);
                return flush.IsCompletedSuccessfully && _connection._connectionLoss is null
                    ? flush
                    : AwaitFlushAsync(flush);
            }
            catch (Exception exception) when (_connection._connectionLoss is not null
                && exception is IOException or ConnectionException or InvalidOperationException or OperationCanceledException)
            {
                return ValueTask.FromException<FlushResult>(_connection._connectionLoss!);
            }
        }

        public override ValueTask<FlushResult> WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default)
        {
            _connection.CheckConnectionLoss();
            try
            {
                ValueTask<FlushResult> flush = _inner.WriteAsync(source, cancellationToken);
                return flush.IsCompletedSuccessfully && _connection._connectionLoss is null
                    ? flush
                    : AwaitFlushAsync(flush);
            }
            catch (Exception exception) when (_connection._connectionLoss is not null
                && exception is IOException or ConnectionException or InvalidOperationException or OperationCanceledException)
            {
                return ValueTask.FromException<FlushResult>(_connection._connectionLoss!);
            }
        }

        public override void Complete(Exception? exception = null)
        {
            _connection.CompleteOutput(exception);
            _connection.OnOutputCompletedByHolder();

            if (exception is not null)
            {
                _connection.SignalPeer();
            }

            _connection._streamEnd?.NotifyClosedIfEnded();
        }

        public override ValueTask CompleteAsync(Exception? exception = null)
        {
            Complete(exception);
            return ValueTask.CompletedTask;
        }

        private async ValueTask<FlushResult> AwaitFlushAsync(ValueTask<FlushResult> flush)
        {
            FlushResult result;
            try
            {
                result = await flush.ConfigureAwait(false);
            }
            catch (Exception exception) when (_connection._connectionLoss is not null
                && exception is IOException or ConnectionException or InvalidOperationException or OperationCanceledException)
            {
                throw _connection._connectionLoss!;
            }

            _connection.CheckConnectionLoss();
            return result;
        }

        private sealed class DebugView
        {
            private readonly SignalingPipeWriter _target;

            public DebugView(SignalingPipeWriter value)
            {
                _target = value;
            }

            public bool CanGetUnflushedBytes => _target.CanGetUnflushedBytes;
            public long UnflushedBytes => _target.UnflushedBytes;
        }
    }

    /// <summary>
    /// A delegating <see cref="PipeReader"/> for an end of a multiplexed stream: completing it with an
    /// error stops the peer's sending half, which the other end is told about. Once the end abandons its
    /// receiving direction (<see cref="IMultiplexedStreamAbort.AbortRead(long)"/>), a read in flight and every
    /// later read fail with <see cref="ConnectionAbortedException"/>, as a QUIC stream's reads fail after
    /// its own <c>STOP_SENDING</c>.
    /// </summary>
    [global::System.Diagnostics.DebuggerDisplay("SignalingPipeReader")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
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
            if (!_connection._readAborted && _connection._connectionLoss is null)
            {
                try
                {
                    _inner.AdvanceTo(consumed);
                }
                catch (InvalidOperationException) when (_connection._connectionLoss is not null)
                {
                    // A buffer acquired before the loss no longer needs advancing after teardown.
                }
            }
        }

        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
        {
            if (!_connection._readAborted && _connection._connectionLoss is null)
            {
                try
                {
                    _inner.AdvanceTo(consumed, examined);
                }
                catch (InvalidOperationException) when (_connection._connectionLoss is not null)
                {
                    // A buffer acquired before the loss no longer needs advancing after teardown.
                }
            }
        }

        public override void CancelPendingRead()
        {
            if (!_connection._readAborted && _connection._connectionLoss is null)
            {
                try
                {
                    _inner.CancelPendingRead();
                }
                catch (InvalidOperationException) when (_connection._connectionLoss is not null)
                {
                    // Connection teardown already woke and completed the pending operation.
                }
            }
        }

        public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            if (_connection._connectionLoss is { } loss)
            {
                return ValueTask.FromException<ReadResult>(loss);
            }

            if (_connection._readAborted)
            {
                return ValueTask.FromException<ReadResult>(ReadAborted());
            }

            try
            {
                ValueTask<ReadResult> read = _inner.ReadAsync(cancellationToken);

                return read.IsCompletedSuccessfully && !_connection._readAborted && _connection._connectionLoss is null
                    ? read
                    : AwaitReadAsync(read);
            }
            catch (Exception exception) when (_connection._connectionLoss is not null
                && exception is IOException or ConnectionException or InvalidOperationException or OperationCanceledException)
            {
                return ValueTask.FromException<ReadResult>(_connection._connectionLoss!);
            }
        }

        public override bool TryRead(out ReadResult result)
        {
            _connection.CheckConnectionLoss();
            if (_connection._readAborted)
            {
                throw ReadAborted();
            }

            try
            {
                bool available = _inner.TryRead(out result);
                _connection.CheckConnectionLoss();
                return available;
            }
            catch (Exception exception) when (_connection._connectionLoss is not null
                && exception is IOException or ConnectionException or InvalidOperationException or OperationCanceledException)
            {
                throw _connection._connectionLoss!;
            }
        }

        public override void Complete(Exception? exception = null)
        {
            _connection.CompleteInput(exception);

            if (exception is not null)
            {
                _connection.SignalPeer();
            }

            _connection._streamEnd?.NotifyClosedIfEnded();
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
            catch (Exception exception) when (_connection._connectionLoss is not null
                && exception is IOException or ConnectionException or InvalidOperationException or OperationCanceledException)
            {
                throw _connection._connectionLoss!;
            }
            catch (InvalidOperationException) when (_connection._readAborted)
            {
                throw ReadAborted();
            }

            _connection.CheckConnectionLoss();
            if (_connection._readAborted)
            {
                throw ReadAborted();
            }

            return result;
        }

        private sealed class DebugView
        {
            public DebugView(SignalingPipeReader value)
            {
            }
        }
    }

    /// <summary>
    /// A <see cref="PipeWriter"/> whose write operations throw, used for the send side of a read-only
    /// (inbound unidirectional) stream, where writing is not permitted.
    /// </summary>
    [global::System.Diagnostics.DebuggerDisplay("ThrowingPipeWriter")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
    private sealed class ThrowingPipeWriter : PipeWriter
    {
        public static ThrowingPipeWriter Instance { get; } = new();

        private static InvalidOperationException Fail()
            => new(ErrorMessages.ConnectionReadOnly);

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

        private sealed class DebugView
        {
            private readonly ThrowingPipeWriter _target;

            public DebugView(ThrowingPipeWriter value)
            {
                _target = value;
            }

            public bool CanGetUnflushedBytes => _target.CanGetUnflushedBytes;
            public long UnflushedBytes => _target.UnflushedBytes;
        }
    }

    private sealed class DebugView
    {
        private readonly InMemoryConnection _target;

        public DebugView(InMemoryConnection value)
        {
            _target = value;
        }

        public ConnectionId Id => _target.Id;
        public EndPoint? LocalEndPoint => _target.LocalEndPoint;
        public EndPoint? RemoteEndPoint => _target.RemoteEndPoint;
        public PipeReader Input => _target.Input;
        public PipeWriter Output => _target.Output;
        public ConnectionDirection Direction => _target.Direction;
        public ConnectionCapabilities Capabilities => _target.Capabilities;
        public ConnectionState State => _target.State;
        public CancellationToken ConnectionClosed => _target.ConnectionClosed;
        public global::Assimalign.Cohesion.Connections.IConnectionFeatureCollection? InnerFeatures => _target.InnerFeatures;
    }
}
