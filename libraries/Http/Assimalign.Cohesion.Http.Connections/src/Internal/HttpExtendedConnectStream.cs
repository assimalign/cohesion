using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// The duplex stream an accepted extended CONNECT (RFC 8441, RFC 9220) surrenders to the application:
/// reads return the peer's <c>DATA</c>, writes send <c>DATA</c>, and closing ends the server's side of
/// the stream. This base owns what HTTP/2 and HTTP/3 share — the head-commit flag, the one-shot close,
/// the closed state, and the failure contract — and forwards the framing to the per-version
/// subclasses.
/// </summary>
/// <remarks>
/// <para>
/// <b>Failures.</b> Whatever ends the exchange underneath the tunnel — the peer resetting the stream,
/// the connection closing, the exchange being cancelled — faults a read or write with an
/// <see cref="IOException"/>, even where the transport reports it as a cancellation. An
/// <see cref="OperationCanceledException"/> surfaces only for the caller's own token, and a read or
/// write the close interrupted, or one started after it, throws <see cref="ObjectDisposedException"/>.
/// </para>
/// <para>
/// <b>Closing.</b> <see cref="Stream.Dispose()"/> starts ending the server's side and returns without
/// waiting for the wire. The BCL WebSocket disposes its stream synchronously, under its own state lock,
/// so a dispose that blocked on the connection's write gate would hold that lock. Closing first wakes
/// any read or write still waiting, then writes the end of the stream. <see cref="DisposeAsync"/> and
/// the transport's send path wait for the end to be written.
/// </para>
/// </remarks>
internal abstract class HttpExtendedConnectStream : Stream
{
    // Cancelled when the stream closes, so a read or write still waiting — for the peer's DATA, for
    // flow-control credit, for the write gate — ends instead of outliving the stream.
    private readonly CancellationTokenSource _closing = new();
    private readonly Lock _gate = new();
    private Task? _closeTask;
    private volatile bool _closed;
    private volatile bool _headCommitted;

    /// <summary>
    /// Gets whether the <c>200</c> response head that established the tunnel is on the wire. Until it
    /// is, nothing may be written for the tunnel, not even the end of the stream.
    /// </summary>
    public bool IsHeadCommitted => _headCommitted;

    /// <summary>
    /// Gets whether the stream was closed — by the application, by the transport when the exchange
    /// ended, or abandoned because the exchange is being reset.
    /// </summary>
    public bool IsClosed => _closed;

    /// <inheritdoc />
    public override bool CanRead => !_closed;

    /// <inheritdoc />
    public override bool CanWrite => !_closed;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException("An extended CONNECT tunnel has no length.");

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException("An extended CONNECT tunnel is not seekable.");
        set => throw new NotSupportedException("An extended CONNECT tunnel is not seekable.");
    }

    /// <summary>
    /// Records that the <c>200</c> response head is on the wire. Called once, by the accept path.
    /// </summary>
    public void MarkHeadCommitted() => _headCommitted = true;

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_closed, this);

        (CancellationToken token, CancellationTokenSource? linked) = LinkClosing(cancellationToken);

        try
        {
            return await ReadCoreAsync(buffer, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            throw TranslateCancellation(exception, cancellationToken);
        }
        finally
        {
            linked?.Dispose();
        }
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_closed, this);

        if (buffer.IsEmpty)
        {
            return;
        }

        (CancellationToken token, CancellationTokenSource? linked) = LinkClosing(cancellationToken);

        try
        {
            await WriteCoreAsync(buffer, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            throw TranslateCancellation(exception, cancellationToken);
        }
        finally
        {
            linked?.Dispose();
        }
    }

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Completes at once: every write is already on the wire when it returns.
    /// </summary>
    public override Task FlushAsync(CancellationToken cancellationToken)
        => cancellationToken.IsCancellationRequested ? Task.FromCanceled(cancellationToken) : Task.CompletedTask;

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException("An extended CONNECT tunnel is not seekable.");

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException("An extended CONNECT tunnel has no length.");

    /// <summary>
    /// Ends the server's side of the tunnel, once: wakes any read or write still waiting, then writes
    /// the end of the stream (HTTP/2 <c>END_STREAM</c>, HTTP/3 FIN). Idempotent; every caller observes
    /// the same completion. A stream or connection that is already gone leaves nothing to end, so the
    /// returned task completes rather than faults in that case.
    /// </summary>
    /// <returns>A task that completes once the end of the stream is written or found unnecessary.</returns>
    public Task CloseAsync()
    {
        TaskCompletionSource? closer = null;
        Task closeTask;

        lock (_gate)
        {
            _closed = true;

            if (_closeTask is null)
            {
                closer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _closeTask = closer.Task;
            }

            closeTask = _closeTask;
        }

        if (closer is not null)
        {
            _closing.Cancel();
            _ = RunCloseAsync(closer);
        }

        return closeTask;
    }

    /// <summary>
    /// Marks the stream closed without ending the server's side: the transport is resetting the stream
    /// instead (the exchange was cancelled, or the response head never reached the wire). Wakes any read
    /// or write still waiting. A close already under way is left to finish.
    /// </summary>
    public void Abandon()
    {
        bool cancel = false;

        lock (_gate)
        {
            _closed = true;

            if (_closeTask is null)
            {
                _closeTask = Task.CompletedTask;
                cancel = true;
            }
        }

        if (cancel)
        {
            _closing.Cancel();
        }
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        Dispose();
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Starts the close without waiting for it (see the type remarks).
            _ = CloseAsync();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Reads the peer's next octets. Returns 0 only when the peer ended its side of the stream; a reset
    /// or a lost connection must fault instead, never surface as the end of the stream.
    /// </summary>
    /// <param name="buffer">The destination.</param>
    /// <param name="cancellationToken">The caller's token combined with the stream's closing.</param>
    /// <returns>The number of octets read; 0 at the end of the peer's side.</returns>
    protected abstract ValueTask<int> ReadCoreAsync(Memory<byte> buffer, CancellationToken cancellationToken);

    /// <summary>
    /// Sends <paramref name="data"/> as <c>DATA</c>, completing once it is on the wire. Waits only for
    /// flow-control credit and for the connection's write gate; a frame is never cut short once begun.
    /// </summary>
    /// <param name="data">The non-empty octets to send.</param>
    /// <param name="cancellationToken">The caller's token combined with the stream's closing.</param>
    /// <returns>A task that completes once the octets are on the wire.</returns>
    protected abstract ValueTask WriteCoreAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>
    /// Writes the end of the server's side of the stream. Called at most once, and only after the head
    /// was committed.
    /// </summary>
    /// <returns>A task that completes once the end of the stream is written.</returns>
    protected abstract ValueTask CloseCoreAsync();

    private async Task RunCloseAsync(TaskCompletionSource closer)
    {
        try
        {
            if (_headCommitted)
            {
                await CloseCoreAsync().ConfigureAwait(false);
            }

            closer.TrySetResult();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or OperationCanceledException)
        {
            // The stream or its connection is already gone (a wire failure, a pipe completed or disposed
            // underneath, a teardown): there is no side left to end.
            closer.TrySetResult();
        }
        catch (Exception exception)
        {
            closer.TrySetException(exception);
        }
    }

    private (CancellationToken Token, CancellationTokenSource? Linked) LinkClosing(CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            return (_closing.Token, null);
        }

        CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closing.Token);
        return (linked.Token, linked);
    }

    private Exception TranslateCancellation(OperationCanceledException exception, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return new OperationCanceledException(exception.Message, exception, cancellationToken);
        }

        if (_closed)
        {
            return new ObjectDisposedException(
                GetType().FullName,
                "The extended CONNECT tunnel was closed while the operation was pending.");
        }

        // The transport cancelled the operation because the exchange ended underneath it: the peer reset
        // the stream, the connection closed, or the exchange was cancelled.
        return new IOException(
            "The extended CONNECT tunnel was aborted: its stream was reset, its connection closed, or the exchange was cancelled.",
            exception);
    }
}
