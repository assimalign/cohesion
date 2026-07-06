using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// The transport's raw response body sink — a write-only <see cref="Stream"/> that owns the
/// response-lifecycle state machine (commit-the-head-once on the first write/flush, idempotent
/// completion) and forwards the wire-specific framing to a small set of <see langword="protected"/>
/// hooks each protocol overrides. It is exposed to response features through
/// <see cref="HttpResponseInterceptorContext.ResponseBody"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is where the streaming write path lives now: a feature package (for example
/// <c>Assimalign.Cohesion.Http.Streaming</c>) wraps this stream to present a typed streaming API,
/// but the framing (HTTP/1.1 chunked, HTTP/2 / HTTP/3 <c>DATA</c> frames, flow control) and the
/// header-commit timing are the transport's concern and stay here. The header block is committed to
/// the wire exactly once, when the first write or flush starts the response; after that the response
/// status and headers are effectively locked because they are already on the wire.
/// </para>
/// <para>
/// The stream is single-consumer (like any <see cref="Stream"/>): one exchange's response is written
/// by one logical caller. Completion (the wire terminator — zero-length chunk / <c>END_STREAM</c>) is
/// driven by the transport's send path via <see cref="CompleteAsync"/> when the exchange finishes.
/// </para>
/// </remarks>
internal abstract class HttpResponseBodyStream : Stream
{
    private bool _started;
    private bool _completed;

    /// <summary>
    /// Whether the response has started — the head has been (or is being) committed because at least
    /// one write or flush has run. The transport's send path uses this to decide whether the exchange
    /// streamed (finalize this sink) or took the buffered path.
    /// </summary>
    public bool HasStarted => _started;

    /// <inheritdoc />
    public override bool CanRead => false;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => !_completed;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfCompleted();
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);

        if (!buffer.IsEmpty)
        {
            await WriteFramedAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
        => WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    /// <inheritdoc />
    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (_completed)
        {
            return;
        }

        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        await FlushFramedAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override void Flush() => FlushAsync(CancellationToken.None).GetAwaiter().GetResult();

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <summary>
    /// Finalizes the response body, emitting the transport's end-of-body marker. Called by the
    /// transport's send path when the exchange completes. Starts the response first if it never
    /// started (so an empty streamed response still emits a valid head + terminator). Idempotent.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the completion.</param>
    /// <returns>A task that completes when the body has been finalized on the wire.</returns>
    internal async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        if (_completed)
        {
            return;
        }

        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        _completed = true;
        await CompleteFramedAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Commits the response status line and header block to the transport. Called once, on start.</summary>
    protected abstract ValueTask CommitHeadersAsync(CancellationToken cancellationToken);

    /// <summary>Frames and writes a non-empty chunk of body bytes, awaiting flow-control credit as needed.</summary>
    protected abstract ValueTask WriteFramedAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>Pushes any buffered body bytes through to the transport so the peer can observe them.</summary>
    protected abstract ValueTask FlushFramedAsync(CancellationToken cancellationToken);

    /// <summary>Emits the transport's end-of-body marker (zero-length chunk / <c>END_STREAM</c>).</summary>
    protected abstract ValueTask CompleteFramedAsync(CancellationToken cancellationToken);

    private async ValueTask EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        await CommitHeadersAsync(cancellationToken).ConfigureAwait(false);
    }

    private void ThrowIfCompleted()
    {
        if (_completed)
        {
            throw new InvalidOperationException(
                "The streaming response has already been completed; no further body bytes can be written.");
        }
    }
}
