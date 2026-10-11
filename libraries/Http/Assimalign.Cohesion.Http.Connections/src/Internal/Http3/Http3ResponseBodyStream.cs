using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// HTTP/3 raw response body sink. Commits the HEADERS frame on first write/flush and emits the body
/// as incremental <c>DATA</c> frames (RFC 9114 §7.2) flushed through to the QUIC request stream.
/// </summary>
/// <remarks>
/// <para>
/// Backpressure is inherent: the QUIC transport applies per-stream flow control on the underlying
/// <see cref="Stream"/> write, so a full window blocks the write until the peer grants more credit —
/// no manual window accounting is needed here. The response body is delimited by the QUIC stream
/// end (RFC 9114 §4.1): completion flushes the last <c>DATA</c> frame and then ends the request
/// stream's write side (a graceful FIN via the <see cref="IConnection"/> half-close contract), so a
/// real HTTP/3 client observes the streamed body terminate rather than waiting on connection
/// teardown (which it would surface as <c>H3_CLOSED_CRITICAL_STREAM</c>). Staged trailers go out as a
/// HEADERS frame just before that end (RFC 9114 §4.1). A response without content — to <c>HEAD</c>
/// (RFC 9110 §9.3.2), or a <c>204</c> or <c>304</c> (<see cref="HttpResponseContentRules"/>) — commits its
/// HEADERS frame but no DATA frame and no trailers.
/// </para>
/// <para>
/// Every write and flush is held to the response's minimum data rate (#1384), through the connection's
/// <see cref="Http3ConnectionContext.WriteResponseFrameAsync"/> and
/// <see cref="Http3ConnectionContext.FlushResponseAsync"/>: a peer that withholds QUIC credit, or reads
/// below the rate, has the request stream reset with <c>H3_REQUEST_CANCELLED</c>, and the write throws an
/// <see cref="OperationCanceledException"/> while the exchange's <c>RequestCancelled</c> fires.
/// </para>
/// </remarks>
internal sealed class Http3ResponseBodyStream : HttpResponseBodyStream
{
    private readonly Http3Context _context;
    private readonly Http3ConnectionContext _connection;
    private readonly Stream _stream;
    private bool _suppressBody;

    public Http3ResponseBodyStream(Http3Context context, Http3ConnectionContext connection)
        : base(context)
    {
        _context = context;
        _connection = connection;
        _stream = context.StreamConnection.AsStream();
    }

    protected override async ValueTask CommitHeadersAsync(CancellationToken cancellationToken)
    {
        // RFC 9110 §15.2 — the final (streamed) response head must not carry a 1xx status.
        HttpInterimResponseRules.EnsureFinalStatusCode(_context.Response.StatusCode);

        // RFC 9110 §9.3.2 — a HEAD response carries the header section a GET would but never content,
        // so every body write after the head is dropped: no DATA frame reaches the wire. A 204 or 304
        // ends with its head the same way (§15.3.5, §15.4.5; #1073).
        _suppressBody = !HttpResponseContentRules.CarriesContent(_context.Response.StatusCode, _context.Request.Method);

        byte[] headerBlock = Http3HeaderCodec.EncodeResponseHeaders(_context);

        // #1385 — a full-duplex handler's first body read may be writing the automatic 100 Continue on another
        // task: the head takes the request stream's writer after it, so the 100 goes out whole and first.
        await _connection.ClaimFinalResponseHeadAsync(_context, cancellationToken).ConfigureAwait(false);
        await _connection.WriteResponseFrameAsync(_context, _stream, Http3FrameType.Headers, headerBlock, cancellationToken).ConfigureAwait(false);
        await _connection.FlushResponseAsync(_context, _stream, cancellationToken).ConfigureAwait(false);
    }

    protected override async ValueTask WriteFramedAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (_suppressBody)
        {
            return;
        }

        await _connection.WriteResponseFrameAsync(_context, _stream, Http3FrameType.Data, data, cancellationToken).ConfigureAwait(false);
        await _connection.FlushResponseAsync(_context, _stream, cancellationToken).ConfigureAwait(false);
    }

    protected override ValueTask FlushFramedAsync(CancellationToken cancellationToken)
        => _connection.FlushResponseAsync(_context, _stream, cancellationToken);

    protected override async ValueTask CompleteFramedAsync(CancellationToken cancellationToken)
    {
        // RFC 9114 §4.1 — staged trailers ride a HEADERS frame after the last DATA frame, before the
        // FIN. A response to HEAD carries none, as on HTTP/2. The section is encoded before its frame
        // starts, so a refused field (#1183) leaves no partial frame for the send path's reset to follow.
        if (!_suppressBody && _context.Response.StagedTrailers is { } trailers)
        {
            byte[] trailerBlock = Http3HeaderCodec.EncodeTrailers(trailers);
            await _connection.WriteResponseFrameAsync(_context, _stream, Http3FrameType.Headers, trailerBlock, cancellationToken).ConfigureAwait(false);
        }

        await _connection.FlushResponseAsync(_context, _stream, cancellationToken).ConfigureAwait(false);

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
}
