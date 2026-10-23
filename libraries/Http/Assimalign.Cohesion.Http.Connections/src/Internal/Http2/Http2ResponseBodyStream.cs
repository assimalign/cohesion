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
/// hook is a no-op. Completion ends the stream with the trailer section's HEADERS frame when the
/// application staged trailers (RFC 9113 §8.1), and with an empty DATA frame otherwise. A response
/// without content — to HEAD (RFC 9110 §9.3.2), or a 204 or 304 (<see cref="HttpResponseContentRules"/>) —
/// commits a HEADERS frame carrying <c>END_STREAM</c> and discards every body write and its trailers,
/// matching the HTTP/1.1 sink.
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("Http2ResponseBodyStream")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
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
        // so its HEADERS frame ends the stream and every later body write is discarded. A 204 or 304
        // ends with its head the same way (§15.3.5, §15.4.5; #1073).
        _suppressBody = !HttpResponseContentRules.CarriesContent(_context.Response.StatusCode, _context.Request.Method);
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

    private sealed class DebugView
    {
        private readonly Http2ResponseBodyStream _target;

        public DebugView(Http2ResponseBodyStream value)
        {
            _target = value;
        }

        public bool HasStarted => _target.HasStarted;
        public bool CanRead => _target.CanRead;
        public bool CanSeek => _target.CanSeek;
        public bool CanWrite => _target.CanWrite;
        public bool CanTimeout => _target.CanTimeout;
        public int ReadTimeout => _target.ReadTimeout;
        public int WriteTimeout => _target.WriteTimeout;
    }
}
