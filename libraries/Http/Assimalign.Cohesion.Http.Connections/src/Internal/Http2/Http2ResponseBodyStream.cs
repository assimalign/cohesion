using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http2;

/// <summary>
/// HTTP/2 raw response body sink. Commits the HEADERS block on first write/flush and emits the body
/// as incremental <c>DATA</c> frames flushed through to the transport, honoring the peer's
/// connection- and stream-level flow-control windows (RFC 9113 §5.2).
/// </summary>
/// <remarks>
/// The sink holds no wire state of its own — it forwards to the owning
/// <see cref="Http2ConnectionContext"/>, which owns the write lock, the HPACK encoder, and the
/// send-window accounting. Each body write already flushes the underlying transport, so the flush
/// hook is a no-op.
/// </remarks>
internal sealed class Http2ResponseBodyStream : HttpResponseBodyStream
{
    private readonly Http2ConnectionContext _connection;
    private readonly Http2Context _context;

    public Http2ResponseBodyStream(Http2ConnectionContext connection, Http2Context context)
    {
        _connection = connection;
        _context = context;
    }

    protected override ValueTask CommitHeadersAsync(CancellationToken cancellationToken)
        => new(_connection.WriteStreamingHeadersAsync(_context, cancellationToken));

    protected override ValueTask WriteFramedAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        => new(_connection.WriteStreamingDataAsync(_context, data, cancellationToken));

    protected override ValueTask FlushFramedAsync(CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    protected override ValueTask CompleteFramedAsync(CancellationToken cancellationToken)
        => new(_connection.CompleteStreamingAsync(_context, cancellationToken));
}
