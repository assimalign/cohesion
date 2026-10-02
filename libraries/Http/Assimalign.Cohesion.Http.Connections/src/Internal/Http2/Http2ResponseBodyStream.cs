using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// HTTP/2 raw response body sink. Commits the HEADERS block on first write/flush and emits the body
/// as incremental <c>DATA</c> frames flushed through to the transport, honoring the peer's
/// connection- and stream-level flow-control windows (RFC 9113 §5.2).
/// </summary>
/// <remarks>
/// The sink holds no wire state of its own — it forwards to the owning
/// <see cref="Http2ConnectionContext"/>, which owns the write lock, the HPACK encoder, and the
/// send-window accounting. Each body write already flushes the underlying transport, so the flush
/// hook is a no-op. A response to HEAD (RFC 9110 §9.3.2) commits a HEADERS frame carrying
/// <c>END_STREAM</c> and discards every body write, matching the HTTP/1.1 sink.
/// </remarks>
internal sealed class Http2ResponseBodyStream : HttpResponseBodyStream
{
    private readonly Http2ConnectionContext _connection;
    private readonly Http2Context _context;
    private bool _suppressBody;

    public Http2ResponseBodyStream(Http2ConnectionContext connection, Http2Context context)
        : base(context)
    {
        _connection = connection;
        _context = context;
    }

    protected override ValueTask CommitHeadersAsync(CancellationToken cancellationToken)
    {
        // RFC 9110 §9.3.2 — a HEAD response carries the header section a GET would but never content,
        // so its HEADERS frame ends the stream and every later body write is discarded.
        _suppressBody = _context.Request.Method == HttpMethod.Head;
        return new(_connection.WriteStreamingHeadersAsync(_context, endStream: _suppressBody, cancellationToken));
    }

    protected override ValueTask WriteFramedAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        => _suppressBody
            ? ValueTask.CompletedTask
            : new(_connection.WriteStreamingDataAsync(_context, data, cancellationToken));

    protected override ValueTask FlushFramedAsync(CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    protected override ValueTask CompleteFramedAsync(CancellationToken cancellationToken)
        => new(_connection.CompleteStreamingAsync(_context, cancellationToken));
}
