using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// HTTP/1.1 raw response body sink. Commits the response head on first write/flush and frames
/// incremental writes with chunked transfer coding (RFC 9112 §7.1) when the caller left
/// <c>Content-Length</c> unset; otherwise the body streams with identity framing. A response without
/// content (a response to <c>HEAD</c>, a <c>204</c>, or a <c>304</c>; <see cref="HttpResponseContentRules"/>)
/// commits its head and discards every body write.
/// </summary>
/// <remarks>
/// Chunked framing is self-delimiting, so the connection can stay keep-alive after a streamed
/// response. The connection stream is owned by the connection, so this sink never disposes it — it
/// only writes and flushes. When a minimum response data rate applies (the listener's, or the one the
/// request set for itself, #1384), every write and flush of the head, the body and the terminator is
/// bounded: a reader that fails to drain the response below that rate closes the connection, which is
/// HTTP/1.1's only reset, and the exchange is cancelled (<see cref="Http1ConnectionContext.FailSlowResponse"/>)
/// instead of blocking the server indefinitely.
/// </remarks>
internal sealed class Http1ResponseBodyStream : HttpResponseBodyStream
{
    // RFC 9112 §7.1 — the terminating zero-length chunk plus the (empty) trailer section: "0" CRLF CRLF.
    private static readonly byte[] _lastChunk = Encoding.ASCII.GetBytes("0\r\n\r\n");
    private static readonly byte[] _crlf = { (byte)'\r', (byte)'\n' };

    private readonly Stream _stream;
    private readonly IConnection _connection;
    private readonly Http1Context _context;
    private readonly HttpMinDataRate? _listenerRate;
    private readonly TimeProvider _timeProvider;
    private readonly string? _altSvcHeaderValue;
    // The gate the response is held to, resolved at the head commit: the request's own rate when a parse
    // context carries one, else the listener's. Null when the response is held to no rate.
    private MinDataRateGate? _gate;
    private bool _chunked;
    private bool _suppressBody;

    /// <summary>
    /// Initializes the sink over the connection stream.
    /// </summary>
    /// <param name="stream">The connection stream the response is written to.</param>
    /// <param name="connection">The connection, aborted when the reader falls below the minimum data rate.</param>
    /// <param name="context">The exchange whose response this is.</param>
    /// <param name="minResponseDataRate">The listener's minimum response data rate, used unless the request changed its own.</param>
    /// <param name="timeProvider">The clock the rate is measured on.</param>
    /// <param name="altSvcHeaderValue">The Alt-Svc value advertised on the response, or <see langword="null"/>.</param>
    public Http1ResponseBodyStream(Stream stream, IConnection connection, Http1Context context, HttpMinDataRate? minResponseDataRate, TimeProvider timeProvider, string? altSvcHeaderValue)
        : base(context)
    {
        _stream = stream;
        _connection = connection;
        _context = context;
        _listenerRate = minResponseDataRate;
        _timeProvider = timeProvider;
        _altSvcHeaderValue = altSvcHeaderValue;
    }

    protected override async ValueTask CommitHeadersAsync(CancellationToken cancellationToken)
    {
        HttpHeaderCollection headers = _context.Response.Headers;

        // Advertise the HTTP/3 endpoint on this streamed response unless the application set its own
        // Alt-Svc (RFC 7838 — the server never overwrites an application value).
        HttpAltServiceInjector.Inject(headers, _altSvcHeaderValue);

        // RFC 9110 §9.3.2 — a HEAD response carries the same header section a GET would but never a
        // body, so suppress every body write and the terminator. A 204 or 304 ends with its head as well
        // (RFC 9112 §6.3, #1073): a chunk written after it would be read as the next response.
        _suppressBody = !HttpResponseContentRules.CarriesContent(_context.Response.StatusCode, _context.Request.Method);

        // Chunked transfer coding is selected when the caller did not commit to a Content-Length,
        // which is the streaming case (length not known up front). A 204 or 304 has no body to code, and
        // a 204 MUST NOT carry Transfer-Encoding (RFC 9112 §6.1); the head encoder also drops one the
        // application set on a 204. HEAD keeps the coding a GET would get.
        bool addedTransferEncoding = false;

        if (!headers.ContainsKey(HttpHeaderKey.ContentLength)
            && !HttpResponseContentRules.IsContentlessStatus(_context.Response.StatusCode))
        {
            _chunked = true;
            if (!headers.ContainsKey(HttpHeaderKey.TransferEncoding))
            {
                headers[HttpHeaderKey.TransferEncoding] = "chunked";
                addedTransferEncoding = true;
            }
        }

        if (!_context.KeepAlive && !headers.ContainsKey(HttpHeaderKey.Connection))
        {
            headers[HttpHeaderKey.Connection] = "close";
        }

        ReadOnlyMemory<byte> head;

        try
        {
            head = Http1MessageWriter.EncodeHead(_context.Response.StatusCode, headers, _context.Response.GetDateHeader());
        }
        catch (HttpInvalidResponseFieldException)
        {
            // A field the head cannot carry (#1183): nothing was written, and the base stream leaves the
            // response unstarted. The chunked coding chosen for it is withdrawn, or a buffered response
            // sent in its place would carry both Transfer-Encoding and the Content-Length it synthesizes.
            _chunked = false;
            if (addedTransferEncoding)
            {
                headers.Remove(HttpHeaderKey.TransferEncoding);
            }

            throw;
        }

        // The response starts here, so its rate is resolved, and the request's knob frozen, now (#1384).
        // The head itself is held to it: a reader that never drains the connection can stall the head as
        // well as the body.
        _gate = _context.GetResponseDataRateGate(_listenerRate, _timeProvider);

        await WriteToStreamAsync(head, cancellationToken).ConfigureAwait(false);
        await FlushStreamAsync(cancellationToken).ConfigureAwait(false);
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
            await WriteToStreamAsync(_crlf, cancellationToken).ConfigureAwait(false);
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
            await WriteToStreamAsync(_lastChunk, cancellationToken).ConfigureAwait(false);
        }

        await FlushStreamAsync(cancellationToken).ConfigureAwait(false);
    }

    private ValueTask WriteToStreamAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        => WriteOrFlushAsync(data, flush: false, cancellationToken);

    private ValueTask FlushStreamAsync(CancellationToken cancellationToken)
        => WriteOrFlushAsync(ReadOnlyMemory<byte>.Empty, flush: true, cancellationToken);

    private async ValueTask WriteOrFlushAsync(ReadOnlyMemory<byte> data, bool flush, CancellationToken cancellationToken)
    {
        (CancellationToken writeToken, CancellationTokenSource? linked) =
            Http1ConnectionContext.LinkResponseCancellation(_context, cancellationToken);
        try
        {
            writeToken.ThrowIfCancellationRequested();
            if (_gate is null)
            {
                if (flush)
                {
                    await _stream.FlushAsync(writeToken).ConfigureAwait(false);
                }
                else
                {
                    await _stream.WriteAsync(data, writeToken).ConfigureAwait(false);
                }
                return;
            }

            // Credit only bounded pieces, so a reader of none of a large write cannot buy its whole allowance.
            if (!await Http1MessageWriter.TryWriteWithinRateAsync(_stream, data, _gate, writeToken).ConfigureAwait(false))
            {
                throw Http1ConnectionContext.FailSlowResponse(_connection, _context);
            }
        }
        catch (Exception exception) when (Http1ConnectionContext.IsResponsePeerFailure(_connection, _context, exception))
        {
            _context.KeepAlive = false;
            throw _context.AbortResponseForPeer(exception);
        }
        catch (OperationCanceledException exception) when (_context.RequestCancelled.IsCancellationRequested
            || cancellationToken.IsCancellationRequested)
        {
            CancellationToken cancelledToken = _context.ResponseAbortedByClient || !cancellationToken.IsCancellationRequested
                ? _context.RequestCancelled
                : cancellationToken;
            if (exception.CancellationToken == cancelledToken)
            {
                throw;
            }
            throw new OperationCanceledException(exception.Message, exception, cancelledToken);
        }
        finally
        {
            linked?.Dispose();
        }
    }
}
