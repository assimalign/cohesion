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
/// <para>
/// Each half ends on its own (#1330). The pipes leave the <see cref="QuicStream"/> open when they complete,
/// and the halves the holder sees end only their own direction: completing <see cref="Output"/> flushes and
/// sends the FIN (<see cref="QuicStream.CompleteWrites"/>), or <c>RESET_STREAM</c> with the default code
/// when it is completed with an error or its final flush fails, and leaves the stream readable until the peer
/// ends its side. That completion is the first signal for the sending direction, so a later
/// <see cref="AbortWrite(long)"/> does nothing and <see cref="Abort(Exception)"/> stops only the receiving
/// direction: <see cref="QuicStream"/> would still reset the direction until the peer acknowledges the FIN.
/// Completing <see cref="Input"/> stops the receiving direction with the default code (<c>STOP_SENDING</c>),
/// unless it has already ended, and leaves the sending direction alone. Once every usable half has been
/// completed the stream is finished, so it is disposed in the background, and
/// <see cref="DisposeAsync"/> waits for that disposal. Neither completion signals
/// <see cref="ConnectionClosed"/>: a half the holder ends is a half-close, not an abort.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
internal sealed class QuicStreamConnection : Connection, IMultiplexedStreamAbort, IConnectionInputCompletion
{
    // RFC 9000 §16 — an application error code is a variable-length integer.
    private const long maxApplicationErrorCode = (1L << 62) - 1;

    private readonly QuicStream _stream;
    private readonly Task _readsClosed;
    private readonly long _defaultStreamErrorCode;
    private readonly Action<QuicStreamConnection> _onDisposed;
    private readonly Action? _onReleaseStarted;
    private readonly CancellationTokenSource _connectionClosedSource = new();
    private readonly Lock _stateLock = new();
    // Completed once the QuicStream is disposed, whoever started it (see ReleaseStream).
    private readonly TaskCompletionSource _streamReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private volatile ConnectionState _state;
    // Set by AbortRead: every later read of Input fails, buffered octets included.
    private volatile bool _readAborted;
    // Set once completing Output has ended the sending direction (FIN or RESET_STREAM). QuicStream treats the
    // direction as open until the peer acknowledges the FIN and would still reset it, so AbortWrite and Abort
    // consult this flag to keep the first signal for the direction.
    private volatile bool _writesEnded;
    private bool _isDisposed;
    private int _closeReported;
    // The usable halves the holder has not completed yet; the last one to complete releases the stream.
    private int _openHalves;
    // Interlocked one-shot: the QuicStream's disposal has started.
    private int _releaseStarted;

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
    /// <param name="onReleaseStarted">An optional callback invoked once before native stream disposal starts.</param>
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
        Action<QuicStreamConnection> onDisposed,
        Action? onReleaseStarted)
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
        _readsClosed = stream.CanRead ? stream.ReadsClosed : Task.CompletedTask;
        _defaultStreamErrorCode = defaultStreamErrorCode;
        _onDisposed = onDisposed;
        _onReleaseStarted = onReleaseStarted;
        LocalEndPoint = localEndPoint;
        RemoteEndPoint = remoteEndPoint;
        Input = stream.CanRead
            ? new AbortableInput(this, PipeReader.Create(stream, streamOptions.ReaderOptions))
            : PipeReader.Create(Stream.Null);
        Output = stream.CanWrite
            ? new HalfClosingOutput(this, PipeWriter.Create(stream, streamOptions.WriterOptions))
            : UnwritablePipeWriter.Instance;
        _openHalves = (stream.CanRead ? 1 : 0) + (stream.CanWrite ? 1 : 0);
        _state = ConnectionState.Open;

        if (stream.CanRead)
        {
            ObservePeerClosure(_readsClosed);
        }

        if (stream.CanWrite)
        {
            ObservePeerClosure(stream.WritesClosed);
        }

        QuicConnectionEventSource.Log.StreamOpened(Id, connectionId, Direction);
    }

    /// <inheritdoc />
    public bool IsInputCompleted => !_readAborted && _readsClosed.IsCompletedSuccessfully;

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
        // direction once and skips one that has ended. A sending direction ended by completing Output keeps its
        // FIN: QuicStream would still reset it until the peer acknowledges the FIN, so only reading is aborted.
        _stream.Abort(_writesEnded ? QuicAbortDirection.Read : QuicAbortDirection.Both, _defaultStreamErrorCode);

        CancelConnectionClosedToken();
        ReportClosed();

        // An abort ends the stream, including any pending pipe operation. Release its pipes and native
        // handle too: a protocol need not keep an aborted wrapper alive until connection teardown.
        _ = DisposeAsync();
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
        ArgumentOutOfRangeException.ThrowIfNegative(errorCode);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(errorCode, maxApplicationErrorCode);

        // The contract: no effect once Output was completed. QuicStream would still put RESET_STREAM on the wire
        // after the FIN until the peer acknowledges it, and the peer could then discard data sent as complete.
        if (_writesEnded)
        {
            return;
        }

        AbortDirection(QuicAbortDirection.Write, errorCode);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        bool completePipes;
        lock (_stateLock)
        {
            completePipes = !_isDisposed;
            _isDisposed = true;
        }

        if (!completePipes)
        {
            // The first disposer may still be flushing. Do not start native release ahead of it, and
            // do not let connection teardown close before its FIN has reached the peer.
            await _streamReleased.Task.ConfigureAwait(false);
        }
        else
        {
            CompletePipes();

            // Completing the last open half starts release. Wait for that same native shutdown before
            // completing disposal; the owning connection bounds delivery with CloseDeliveryTimeout.
            await ReleaseStream().ConfigureAwait(false);
        }

        CancelConnectionClosedToken();

        lock (_stateLock)
        {
            if (_state != ConnectionState.Aborted)
            {
                _state = ConnectionState.Closed;
            }
        }
    }

    private void CompletePipes()
    {
        try
        {
            Output.Complete();
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

        try
        {
            Input.Complete();
        }
        catch (InvalidOperationException)
        {
            // A pending read was ended by the abort or connection teardown.
        }
        catch (IOException)
        {
            // An output failure must not skip releasing the input pipe.
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

    // The holder completed Output: end the sending direction alone — the FIN after the flushed data, or
    // RESET_STREAM with the default code when Output was completed with an error or its final flush failed.
    // QuicStream ends each direction once, so a direction already aborted with a code keeps it, and from here
    // on no abort of this end resets the direction (_writesEnded).
    private void EndWrites(Exception? exception)
    {
        // Raised before the FIN is queued, so an abort racing the completion cannot reset after it.
        _writesEnded = true;

        try
        {
            if (exception is null)
            {
                _stream.CompleteWrites();
            }
            else
            {
                AbortDirection(QuicAbortDirection.Write, _defaultStreamErrorCode);
            }
        }
        catch (InvalidOperationException)
        {
            // ObjectDisposedException included: the stream was released underneath (an abort or the
            // connection's teardown), so there is no direction left to end.
        }
        catch (QuicException)
        {
            // The stream or its connection has already ended.
        }
        finally
        {
            OnHalfCompleted();
        }
    }

    // The holder completed Input: stop the receiving direction with the default code (STOP_SENDING) unless it
    // has already ended — read to its end, or aborted with a code — and leave the sending direction alone.
    private void EndReads()
    {
        try
        {
            AbortDirection(QuicAbortDirection.Read, _defaultStreamErrorCode);
        }
        finally
        {
            OnHalfCompleted();
        }
    }

    private void OnHalfCompleted()
    {
        if (Interlocked.Decrement(ref _openHalves) == 0)
        {
            // Every usable half is done, so the stream is finished: release it without waiting.
            _ = ReleaseStream();
        }
    }

    // Disposes the QuicStream once, whoever asks first: the completion of the last open half, or DisposeAsync.
    // QuicStream.DisposeAsync ends a direction still open (reads with the default code, writes gracefully)
    // and waits for the stream's shutdown to complete, which a graceful FIN reaches once the peer has the data.
    private Task ReleaseStream()
    {
        if (Interlocked.Exchange(ref _releaseStarted, 1) == 0)
        {
            _onReleaseStarted?.Invoke();
            _ = ReleaseStreamCoreAsync();
        }

        return _streamReleased.Task;
    }

    private async Task ReleaseStreamCoreAsync()
    {
        try
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (QuicException)
        {
            // The connection is gone underneath the stream; there is nothing left to shut down.
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            // Both halves have ended and native shutdown has finished. Keeping the wrapper tracked
            // beyond here retains its pipes and cancellation state for the life of the connection.
            _onDisposed(this);
            ReportClosed();
            _streamReleased.TrySetResult();
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
    /// Completing it ends the receiving direction alone (<see cref="EndReads"/>), once. Everything else passes
    /// straight through, so a read before the abort costs one flag check.
    /// </summary>
    private sealed class AbortableInput : PipeReader
    {
        private readonly QuicStreamConnection _connection;
        private readonly PipeReader _inner;
        private int _completed;

        public AbortableInput(QuicStreamConnection connection, PipeReader inner)
        {
            _connection = connection;
            _inner = inner;
        }

        public override void AdvanceTo(SequencePosition consumed) => _inner.AdvanceTo(consumed);

        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined) => _inner.AdvanceTo(consumed, examined);

        public override void CancelPendingRead() => _inner.CancelPendingRead();

        public override void Complete(Exception? exception = null)
        {
            if (Interlocked.Exchange(ref _completed, 1) == 1)
            {
                return;
            }

            try
            {
                _inner.Complete(exception);
            }
            finally
            {
                _connection.EndReads();
            }
        }

        public override async ValueTask CompleteAsync(Exception? exception = null)
        {
            if (Interlocked.Exchange(ref _completed, 1) == 1)
            {
                return;
            }

            try
            {
                await _inner.CompleteAsync(exception).ConfigureAwait(false);
            }
            finally
            {
                _connection.EndReads();
            }
        }

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

    /// <summary>
    /// The writable stream's <see cref="Output"/>: a <see cref="PipeWriter"/> over the QUIC stream whose
    /// completion ends the sending direction alone (#1330) — it flushes what the pipe holds, then sends the
    /// FIN, or <c>RESET_STREAM</c> with the default code when completed with an error or when that flush
    /// fails, and leaves the stream readable (<see cref="EndWrites"/>), once. Everything else passes straight
    /// through.
    /// </summary>
    private sealed class HalfClosingOutput : PipeWriter
    {
        private readonly QuicStreamConnection _connection;
        private readonly PipeWriter _inner;
        private int _completed;

        public HalfClosingOutput(QuicStreamConnection connection, PipeWriter inner)
        {
            _connection = connection;
            _inner = inner;
        }

        public override bool CanGetUnflushedBytes => _inner.CanGetUnflushedBytes;

        public override long UnflushedBytes => _inner.UnflushedBytes;

        public override void Advance(int bytes) => _inner.Advance(bytes);

        public override Memory<byte> GetMemory(int sizeHint = 0) => _inner.GetMemory(sizeHint);

        public override Span<byte> GetSpan(int sizeHint = 0) => _inner.GetSpan(sizeHint);

        public override void CancelPendingFlush() => _inner.CancelPendingFlush();

        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default) => _inner.FlushAsync(cancellationToken);

        public override ValueTask<FlushResult> WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default) => _inner.WriteAsync(source, cancellationToken);

        public override void Complete(Exception? exception = null)
        {
            if (Interlocked.Exchange(ref _completed, 1) == 1)
            {
                return;
            }

            // A final flush that fails (a write still in flight, a stream already gone) left the data partial,
            // so the direction is reset rather than ended with a FIN.
            Exception? failure = exception;

            try
            {
                _inner.Complete(exception);
            }
            catch (Exception flushFailure)
            {
                failure ??= flushFailure;
                throw;
            }
            finally
            {
                _connection.EndWrites(failure);
            }
        }

        public override async ValueTask CompleteAsync(Exception? exception = null)
        {
            if (Interlocked.Exchange(ref _completed, 1) == 1)
            {
                return;
            }

            Exception? failure = exception;

            try
            {
                await _inner.CompleteAsync(exception).ConfigureAwait(false);
            }
            catch (Exception flushFailure)
            {
                failure ??= flushFailure;
                throw;
            }
            finally
            {
                _connection.EndWrites(failure);
            }
        }
    }
}
