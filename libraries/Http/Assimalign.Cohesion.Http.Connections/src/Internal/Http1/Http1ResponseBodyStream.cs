using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http1;

/// <summary>
/// HTTP/1.1 raw response body sink. Commits the response head on first write/flush and frames
/// incremental writes with chunked transfer coding (RFC 9112 §7.1) when the caller left
/// <c>Content-Length</c> unset; otherwise the body streams with identity framing.
/// </summary>
/// <remarks>
/// Chunked framing is self-delimiting, so the connection can stay keep-alive after a streamed
/// response. The connection stream is owned by the connection, so this sink never disposes it — it
/// only writes and flushes.
/// </remarks>
internal sealed class Http1ResponseBodyStream : HttpResponseBodyStream
{
    // RFC 9112 §7.1 — the terminating zero-length chunk plus the (empty) trailer section: "0" CRLF CRLF.
    private static readonly byte[] LastChunk = Encoding.ASCII.GetBytes("0\r\n\r\n");
    private static readonly byte[] Crlf = { (byte)'\r', (byte)'\n' };

    private readonly Stream _stream;
    private readonly Http1Context _context;
    private bool _chunked;
    private bool _suppressBody;

    public Http1ResponseBodyStream(Stream stream, Http1Context context)
    {
        _stream = stream;
        _context = context;
    }

    protected override async ValueTask CommitHeadersAsync(CancellationToken cancellationToken)
    {
        HttpHeaderCollection headers = _context.Response.Headers;

        // RFC 9110 §9.3.2 — a HEAD response carries the same header section a GET would but never a
        // body, so suppress every body write and the terminator.
        _suppressBody = _context.Request.Method == HttpMethod.Head;

        // Chunked transfer coding is selected when the caller did not commit to a Content-Length,
        // which is the streaming case (length not known up front).
        if (!headers.ContainsKey(HttpHeaderKey.ContentLength))
        {
            _chunked = true;
            if (!headers.ContainsKey(HttpHeaderKey.TransferEncoding))
            {
                headers[HttpHeaderKey.TransferEncoding] = "chunked";
            }
        }

        if (!_context.KeepAlive && !headers.ContainsKey(HttpHeaderKey.Connection))
        {
            headers[HttpHeaderKey.Connection] = "close";
        }

        await Http1MessageWriter.WriteHeadAsync(_stream, _context.Response.StatusCode, headers, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async ValueTask WriteFramedAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (_suppressBody)
        {
            return;
        }

        if (_chunked)
        {
            byte[] prefix = Encoding.ASCII.GetBytes(
                data.Length.ToString("x", CultureInfo.InvariantCulture) + "\r\n");
            await _stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
            await _stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            await _stream.WriteAsync(Crlf, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        }
    }

    protected override ValueTask FlushFramedAsync(CancellationToken cancellationToken)
        => new(_stream.FlushAsync(cancellationToken));

    protected override async ValueTask CompleteFramedAsync(CancellationToken cancellationToken)
    {
        if (_chunked && !_suppressBody)
        {
            await _stream.WriteAsync(LastChunk, cancellationToken).ConfigureAwait(false);
        }

        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
