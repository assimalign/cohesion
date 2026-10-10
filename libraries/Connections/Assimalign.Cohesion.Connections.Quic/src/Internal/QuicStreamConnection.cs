using System;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Net.Quic;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.Quic.Internal;

namespace Assimalign.Cohesion.Connections.Quic.Internal;

/// <summary>
/// A single QUIC stream surfaced as a <see cref="Connection"/>.
/// </summary>
/// <remarks>
/// <para>
/// The stream's pipes are created over the parent connection's shared stream pipe options, so
/// every stream of one QUIC connection draws from a single memory pool. A unidirectional stream
/// surfaces a pre-completed input (outbound) or an unwritable output (inbound); the usable
/// halves are captured once in <see cref="Direction"/> at construction.
/// </para>
/// <para>
/// <see cref="ConnectionClosed"/> fires when this end aborts or disposes the stream, and also when the
/// stream ends underneath it: the peer resets the receiving half (<c>RESET_STREAM</c>), stops the
/// sending half (<c>STOP_SENDING</c>), or the QUIC connection is lost. A consumer such as an HTTP/3
/// request learns that its peer abandoned the stream without having to read or write. A half that
/// ends cleanly (the peer's FIN, this end's own completion) does not fire it.
/// </para>
/// <para>
/// The stream carries an application error code per direction through <see cref="IMultiplexedStreamAbort"/>:
/// <see cref="AbortRead(long)"/> sends <c>STOP_SENDING</c> and <see cref="AbortWrite(long)"/> sends
/// <c>RESET_STREAM</c>, each with the caller's code. <see cref="QuicStream"/> ends each direction once, so a
/// direction aborted with a code keeps it when <see cref="Abort(Exception)"/> or disposal later ends the
/// stream with the default code.
/// </para>
/// <para>
/// After <see cref="AbortRead(long)"/> every read of <see cref="Input"/> fails, as the contract requires. The
/// pipe over the QUIC stream would otherwise hand back octets it had already buffered without touching the
/// stream, so <see cref="Input"/> is a thin delegating reader that fails a read once the receiving direction
/// was aborted; a read in flight at the abort fails in <see cref="QuicStream"/> itself.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
internal sealed class QuicStreamConnection : Connection, IMultiplexedStreamAbort
{
    // RFC 9000 §16 — an application error code is a variable-length integer.
    private const long maxApplicationErrorCode = (1L << 62) - 1;

    private readonly QuicStream _stream;
    private readonly long _defaultStreamErrorCode;
    private readonly Action<QuicStreamConnection> _onDisposed;
    private readonly CancellationTokenSource _connectionClosedSource = new();
    private readonly Lock _stateLock = new();

    private volatile ConnectionState _state;
    // Set by AbortRead: every later read of Input fails, buffered octets included.
    private volatile bool _readAborted;
    private bool _isDisposed;
    private int _closeReported;

    /// <summary>
    /// Creates a new connection over the supplied QUIC stream. The connection takes ownership
    /// of the stream but not of the shared <paramref name="streamOptions"/>.
    /// </summary>
    /// <param name="stream">The QUIC stream to wrap.</param>
    /// <param name="connectionId">The identifier of the owning QUIC connection, for diagnostics.</param>
    /// <param name="localEndPoint">The parent connection's local endpoint.</param>
    /// <param name="remoteEndPoint">The parent connection's remote endpoint.</param>
    /// <param name="streamOptions">The parent-owned shared stream pipe options.</param>
    /// <param name="defaultStreamErrorCode">The error code used when the stream is aborted without a caller's code.</param>
    /// <param name="onDisposed">A callback invoked when the stream connection is disposed.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="stream"/>, <paramref name="streamOptions"/>, or
    /// <paramref name="onDisposed"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">Thrown when the stream is neither readable nor writable.</exception>
    public QuicStreamConnection(
        QuicStream stream,
        ConnectionId connectionId,
        EndPoint? localEndPoint,
        EndPoint? remoteEndPoint,
        StreamPipeOptionsContext streamOptions,
        long defaultStreamErrorCode,
        Action<QuicStreamConnection> onDisposed)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(streamOptions);
        ArgumentNullException.ThrowIfNull(onDisposed);

        Direction = (stream.CanRead, stream.CanWrite) switch
        {
            (true, true) => ConnectionDirection.Bidirectional,
            (true, false) => ConnectionDirection.ReadOnly,
            (false, true) => ConnectionDirection.WriteOnly,
            (false, false) => throw new ArgumentException("The QUIC stream is neither readable nor writable.", nameof(stream))
        };

        _stream = stream;
        _defaultStreamErrorCode = defaultStreamErrorCode;
        _onDisposed = onDisposed;
        LocalEndPoint = localEndPoint;
        RemoteEndPoint = remoteEndPoint;
        Input = stream.CanRead
            ? new AbortableInput(this, PipeReader.Create(stream, streamOptions.ReaderOptions))
            : PipeReader.Create(Stream.Null);
        Output = stream.CanWrite
            ? PipeWriter.Create(stream, streamOptions.WriterOptions)
            : UnwritablePipeWriter.Instance;
        _state = ConnectionState.Open;

        if (stream.CanRead)
        {
            ObservePeerClosure(stream.ReadsClosed);
        }

        if (stream.CanWrite)
        {
            ObservePeerClosure(stream.WritesClosed);
        }

        QuicConnectionEventSource.Log.StreamOpened(Id, connectionId, Direction);
    }

    /// <inheritdoc />
    public override ConnectionId Id { get; } = ConnectionId.New();

    /// <inheritdoc />
    public override EndPoint? LocalEndPoint { get; }

    /// <inheritdoc />
    public override EndPoint? RemoteEndPoint { get; }

    /// <inheritdoc />
    public override PipeReader Input { get; }

    /// <inheritdoc />
    public override PipeWriter Output { get; }

    /// <inheritdoc />
    public override ConnectionDirection Direction { get; }

    /// <inheritdoc />
    public override ConnectionCapabilities Capabilities { get; } = new ConnectionCapabilities(
        ConnectionProtocol.Quic,
        ConnectionDelivery.Stream,
        IsReliable: true,
        IsOrdered: true,
        IsMultiplexed: false,
        ConnectionSecurity.Tls);

    /// <inheritdoc />
    public override ConnectionState State => _state;

    /// <inheritdoc />
    public override CancellationToken ConnectionClosed => _connectionClosedSource.Token;

    /// <inheritdoc />
    public override void Abort(Exception? reason = null)
    {
        lock (_stateLock)
        {
            if (_state is ConnectionState.Aborted or ConnectionState.Closed)
            {
                return;
            }

            _state = ConnectionState.Aborted;
        }

        // A direction already aborted through IMultiplexedStreamAbort keeps its code: QuicStream ends each
        // direction once and skips one that has ended.
        _stream.Abort(QuicAbortDirection.Both, _defaultStreamErrorCode);

        CancelConnectionClosedToken();
        ReportClosed();
    }

    /// <inheritdoc />
    public void AbortRead(long errorCode)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(errorCode);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(errorCode, maxApplicationErrorCode);

        // Bar reads before the stream is aborted, so no read can return what the abort discards. A
        // write-only stream has no receiving direction, and its input stays the pre-completed one.
        if (Direction != ConnectionDirection.WriteOnly)
        {
            _readAborted = true;
        }

        AbortDirection(QuicAbortDirection.Read, errorCode);
    }

    /// <inheritdoc />
    public void AbortWrite(long errorCode)
    {
        AbortDirection(QuicAbortDirection.Write, errorCode);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        lock (_stateLock)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
        }

        try
        {
            Output.Complete();
            Input.Complete();
        }
        catch (InvalidOperationException)
        {
            // Best-effort completion: ObjectDisposedException derives from InvalidOperationException,
            // so this single clause covers both an in-progress pipe operation and a disposed stream.
        }
        catch (IOException)
        {
            // Completing a pipe flushes any remaining buffered bytes into the QUIC stream, which
            // throws QuicException (an IOException) when the stream or its connection is already
            // closed — routine for streams released after their owning connection closed.
        }

        await _stream.DisposeAsync().ConfigureAwait(false);

        CancelConnectionClosedToken();

        _onDisposed(this);
        ReportClosed();

        lock (_stateLock)
        {
            if (_state != ConnectionState.Aborted)
            {
                _state = ConnectionState.Closed;
            }
        }
    }

    // QuicStream.Abort skips a direction that has already ended (read to its end, completed, or aborted),
    // and a stream that is disposed. The connection may be gone underneath the stream, which leaves
    // nothing to tell the peer.
    private void AbortDirection(QuicAbortDirection direction, long errorCode)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(errorCode);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(errorCode, maxApplicationErrorCode);

        try
        {
            _stream.Abort(direction, errorCode);
        }
        catch (ObjectDisposedException)
        {
            // The stream was released underneath the call.
        }
        catch (QuicException)
        {
            // The stream or its connection has already ended.
        }
    }

    private void CancelConnectionClosedToken()
    {
        try
        {
            _connectionClosedSource.Cancel();
        }
        catch (AggregateException)
        {
            // Exceptions thrown by ConnectionClosed registrations must not fault teardown.
        }
    }

    // A half of the stream that faults for any reason but this end's own operation was ended by the
    // peer (StreamAborted: RESET_STREAM or STOP_SENDING) or by the loss of the connection. The local
    // paths, Abort and DisposeAsync, signal ConnectionClosed themselves; their faults
    // (OperationAborted) are left alone. The continuation runs on the thread pool, never on the QUIC
    // event thread that completed the task, so ConnectionClosed registrations cannot stall it.
    private void ObservePeerClosure(Task closed)
    {
        _ = closed.ContinueWith(
            static (task, state) =>
            {
                if (task.Exception?.InnerException is QuicException { QuicError: not QuicError.OperationAborted })
                {
                    ((QuicStreamConnection)state!).CancelConnectionClosedToken();
                }
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    // Abort and DisposeAsync both end the stream; whichever comes first reports it, once.
    private void ReportClosed()
    {
        if (Interlocked.Exchange(ref _closeReported, 1) == 0)
        {
            QuicConnectionEventSource.Log.StreamClosed(Id);
        }
    }

    // The exception a read reports after this end aborted its receiving direction: the one QuicStream
    // raises for a read the abort overtakes.
    private static QuicException ReadAborted()
        => new(QuicError.OperationAborted, null, "Reading was aborted on this end of the stream.");

    /// <summary>
    /// The readable stream's <see cref="Input"/>: a <see cref="PipeReader"/> over the QUIC stream that fails
    /// every read once <see cref="AbortRead(long)"/> abandoned the receiving direction. The pipe it delegates
    /// to returns octets it has buffered but the holder has not examined without reading the stream, so the
    /// check is made before each read; a read already waiting on the stream fails in <see cref="QuicStream"/>.
    /// Everything else passes straight through, so a read before the abort costs one flag check.
    /// </summary>
    private sealed class AbortableInput : PipeReader
    {
        private readonly QuicStreamConnection _connection;
        private readonly PipeReader _inner;

        public AbortableInput(QuicStreamConnection connection, PipeReader inner)
        {
            _connection = connection;
            _inner = inner;
        }

        public override void AdvanceTo(SequencePosition consumed) => _inner.AdvanceTo(consumed);

        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined) => _inner.AdvanceTo(consumed, examined);

        public override void CancelPendingRead() => _inner.CancelPendingRead();

        public override void Complete(Exception? exception = null) => _inner.Complete(exception);

        public override ValueTask CompleteAsync(Exception? exception = null) => _inner.CompleteAsync(exception);

        public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            return _connection._readAborted
                ? ValueTask.FromException<ReadResult>(ReadAborted())
                : _inner.ReadAsync(cancellationToken);
        }

        public override bool TryRead(out ReadResult result)
        {
            if (_connection._readAborted)
            {
                throw ReadAborted();
            }

            return _inner.TryRead(out result);
        }

        protected override ValueTask<ReadResult> ReadAtLeastAsyncCore(int minimumSize, CancellationToken cancellationToken)
        {
            return _connection._readAborted
                ? ValueTask.FromException<ReadResult>(ReadAborted())
                : _inner.ReadAtLeastAsync(minimumSize, cancellationToken);
        }
    }
}
