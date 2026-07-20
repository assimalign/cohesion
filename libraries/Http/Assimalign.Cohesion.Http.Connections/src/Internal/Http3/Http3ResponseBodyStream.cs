using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http.Connections.Internal.Http3.Frames;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http3;

/// <summary>
/// HTTP/3 raw response body sink. Commits the HEADERS frame on first write/flush and emits the body
/// as incremental <c>DATA</c> frames (RFC 9114 §7.2) flushed through to the QUIC request stream.
/// </summary>
/// <remarks>
/// Backpressure is inherent: the QUIC transport applies per-stream flow control on the underlying
/// <see cref="Stream"/> write, so a full window blocks the write until the peer grants more credit —
/// no manual window accounting is needed here. The response body is delimited by the QUIC stream
/// end (RFC 9114 §4.1): completion flushes the last <c>DATA</c> frame and then ends the request
/// stream's write side (a graceful FIN via the <see cref="IConnection"/> half-close contract), so a
/// real HTTP/3 client observes the streamed body terminate rather than waiting on connection
/// teardown (which it would surface as <c>H3_CLOSED_CRITICAL_STREAM</c>).
/// </remarks>
internal sealed class Http3ResponseBodyStream : HttpResponseBodyStream
{
    private readonly Http3Context _context;
    private readonly Stream _stream;

    public Http3ResponseBodyStream(Http3Context context)
        : base(context)
    {
        _context = context;
        _stream = context.StreamConnection.AsStream();
    }

    protected override async ValueTask CommitHeadersAsync(CancellationToken cancellationToken)
    {
        // RFC 9110 §15.2 — the final (streamed) response head must not carry a 1xx status.
        HttpInterimResponseRules.EnsureFinalStatusCode(_context.Response.StatusCode);

        byte[] headerBlock = Http3HeaderCodec.EncodeResponseHeaders(_context);
        await WriteFrameAsync(Http3FrameType.Headers, headerBlock, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async ValueTask WriteFramedAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        await WriteFrameAsync(Http3FrameType.Data, data, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override ValueTask FlushFramedAsync(CancellationToken cancellationToken)
        => new(_stream.FlushAsync(cancellationToken));

    protected override async ValueTask CompleteFramedAsync(CancellationToken cancellationToken)
    {
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        // RFC 9114 §4.1 — the streamed body is delimited by the request stream's end. End the write
        // side (graceful QUIC FIN via the IConnection half-close contract) so the client sees the
        // body terminate now; leaving it dangling until connection teardown surfaces at the client as
        // H3_CLOSED_CRITICAL_STREAM. Best-effort: the DATA frames are already flushed, so a teardown
        // race that disposed the stream underneath the completion is benign.
        try
        {
            _context.StreamConnection.Output.Complete();
        }
        catch (InvalidOperationException)
        {
            // ObjectDisposedException derives from InvalidOperationException — the stream was torn
            // down by a concurrent connection abort after the body flushed; the FIN is moot.
        }
        catch (IOException)
        {
            // QuicException is an IOException: the stream/connection is already gone, so the missed
            // FIN rides on the connection close instead.
        }
    }

    private async ValueTask WriteFrameAsync(Http3FrameType frameType, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        // RFC 9114 §7.1 — frame = type (varint) + length (varint) + payload.
        QuicVariableLengthInteger.Write(_stream, (long)frameType);
        QuicVariableLengthInteger.Write(_stream, payload.Length);

        if (!payload.IsEmpty)
        {
            await _stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        }
    }
}
