using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http.Connections.Internal;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http1;

/// <summary>
/// HTTP/1.1 raw response body sink. Commits the response head on first write/flush and frames
/// incremental writes with chunked transfer coding (RFC 9112 §7.1) when the caller left
/// <c>Content-Length</c> unset; otherwise the body streams with identity framing.
/// </summary>
/// <remarks>
/// Chunked framing is self-delimiting, so the connection can stay keep-alive after a streamed
/// response. The connection stream is owned by the connection, so this sink never disposes it — it
/// only writes and flushes. When a minimum response data rate is configured, each body write and
/// flush is bounded: a reader that fails to drain the response below that rate aborts the exchange
/// (RFC 9110 §15.5.9 timeout semantics) instead of blocking the server indefinitely.
/// </remarks>
internal sealed class Http1ResponseBodyStream : HttpResponseBodyStream
{
    // RFC 9112 §7.1 — the terminating zero-length chunk plus the (empty) trailer section: "0" CRLF CRLF.
    private static readonly byte[] LastChunk = Encoding.ASCII.GetBytes("0\r\n\r\n");
    private static readonly byte[] Crlf = { (byte)'\r', (byte)'\n' };

    private readonly Stream _stream;
    private readonly Http1Context _context;
    private readonly MinDataRateGate? _gate;
    private bool _chunked;
    private bool _suppressBody;

    public Http1ResponseBodyStream(Stream stream, Http1Context context, HttpMinDataRate? minResponseDataRate, TimeProvider timeProvider)
        : base(context)
    {
        _stream = stream;
        _context = context;
        _gate = minResponseDataRate is not null ? new MinDataRateGate(minResponseDataRate, timeProvider) : null;
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
            await WriteToStreamAsync(prefix, cancellationToken).ConfigureAwait(false);
            await WriteToStreamAsync(data, cancellationToken).ConfigureAwait(false);
            await WriteToStreamAsync(Crlf, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await WriteToStreamAsync(data, cancellationToken).ConfigureAwait(false);
        }
    }

    protected override ValueTask FlushFramedAsync(CancellationToken cancellationToken)
        => FlushStreamAsync(cancellationToken);

    protected override async ValueTask CompleteFramedAsync(CancellationToken cancellationToken)
    {
        if (_chunked && !_suppressBody)
        {
            await WriteToStreamAsync(LastChunk, cancellationToken).ConfigureAwait(false);
        }

        await FlushStreamAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteToStreamAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (_gate is null)
        {
            await _stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!_gate.TryGetOperationTimeout(out TimeSpan timeout))
        {
            throw ResponseTooSlow();
        }

        using CancellationTokenSource timeoutSource = new(timeout, _gate.TimeProvider);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        long start = _gate.TimeProvider.GetTimestamp();
        try
        {
            await _stream.WriteAsync(data, linked.Token).ConfigureAwait(false);
            _gate.Record(_gate.TimeProvider.GetTimestamp() - start, data.Length);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw ResponseTooSlow();
        }
    }

    private async ValueTask FlushStreamAsync(CancellationToken cancellationToken)
    {
        if (_gate is null)
        {
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!_gate.TryGetOperationTimeout(out TimeSpan timeout))
        {
            throw ResponseTooSlow();
        }

        using CancellationTokenSource timeoutSource = new(timeout, _gate.TimeProvider);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        long start = _gate.TimeProvider.GetTimestamp();
        try
        {
            await _stream.FlushAsync(linked.Token).ConfigureAwait(false);
            _gate.Record(_gate.TimeProvider.GetTimestamp() - start, 0);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw ResponseTooSlow();
        }
    }

    private static IOException ResponseTooSlow()
    {
        // RFC 9110 §15.5.9 — a reader draining the response below the configured minimum data rate
        // is abandoned. The response has already started, so the status cannot change; the exchange
        // is aborted as a wire-level failure.
        return new IOException("The response was written below the configured minimum data rate.");
    }
}
