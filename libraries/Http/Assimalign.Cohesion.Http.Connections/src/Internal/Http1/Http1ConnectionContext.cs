using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http1;

internal sealed class Http1ConnectionContext : HttpStreamConnectionContext
{
    private readonly Http1ConnectionListenerOptions.Http1Limits _limits;
    private readonly IHttpRequestInterceptor[] _interceptors;
    private readonly IHttpResponseInterceptor[] _responseInterceptors;

    public Http1ConnectionContext(IConnection connection, bool isSecure, Http1ConnectionListenerOptions.Http1Limits limits, IHttpRequestInterceptor[] interceptors, IHttpResponseInterceptor[] responseInterceptors)
        : base(connection, isSecure)
    {
        _limits = limits;
        _interceptors = interceptors;
        _responseInterceptors = responseInterceptors;
    }

    /// <summary>
    /// Yields HTTP/1.1 request contexts for the lifetime of this connection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Wire-level failures isolated to this connection &mdash; truncated
    /// request lines, malformed headers, a client speaking TLS to a
    /// plain-HTTP listener, an abruptly dropped socket &mdash; gracefully
    /// terminate the enumerable rather than propagating out. The
    /// application's <c>await foreach</c> exits cleanly, the connection
    /// gets disposed by the surrounding <c>await using</c>, and the
    /// listener continues accepting subsequent connections. Cancellation
    /// (<see cref="OperationCanceledException"/>) and other non-wire
    /// exceptions still propagate so cooperative shutdown and programmer
    /// errors are not masked.
    /// </para>
    /// </remarks>
    public override async IAsyncEnumerable<IHttpContext> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Http1Context? context = await TryReadRequestAsync(cancellationToken).ConfigureAwait(false);

            if (context is null)
            {
                yield break;
            }

            // Expose the raw chunked response body sink to registered response interceptors so a
            // feature package (streaming / SSE) can wrap it and install a typed response feature —
            // without this transport depending on that package. Zero interceptors → buffered fast path.
            // The connection-takeover capability rides the same seam: an HTTP/1.1 exchange owns its
            // whole connection, so a feature package (protocol upgrade / CONNECT tunnelling) may
            // claim the raw stream and finalize the exchange out-of-band. The interim-response
            // capability (100 Continue on demand, 103 Early Hints) rides it too — a feature package
            // (Http.InterimResponses) wraps it and emits interim responses ahead of the final one.
            if (_responseInterceptors.Length > 0)
            {
                context.RunResponseInterceptors(
                    _responseInterceptors,
                    new Http1ResponseBodyStream(Stream, context),
                    new Http1ConnectionTakeover(context, Stream),
                    new Http1InterimResponseWriter(context, Stream));
            }

            yield return context;

            if (!context.KeepAlive)
            {
                yield break;
            }
        }
    }

    public override ValueTask SendAsync(IHttpContext context, CancellationToken cancellationToken = default)
    {
        if (context is not Http1Context http1Context)
        {
            throw new InvalidOperationException("The supplied context does not belong to an HTTP/1.1 connection.");
        }

        // The connection was taken over (accepted protocol upgrade / CONNECT tunnel): the
        // transition response went straight to the surrendered raw stream and the connection no
        // longer speaks HTTP. Checked before the sink branch so a misused streaming feature can
        // never finalize chunked framing into the tunnel (RFC 9110 §7.8 / §9.3.6).
        if (http1Context.ResponseFinalized)
        {
            return ValueTask.CompletedTask;
        }

        // If a response feature streamed to the raw sink, the head and body are already on the
        // wire; finalize (emit the terminating zero-length chunk) rather than writing a second,
        // buffered response.
        if (http1Context.ResponseBodySink is { HasStarted: true } sink)
        {
            return new ValueTask(sink.CompleteAsync(cancellationToken));
        }

        return Http1MessageWriter.WriteResponseAsync(Stream, http1Context, cancellationToken);
    }

    /// <summary>
    /// Attempts to read the next request from the wire. Returns
    /// <see langword="null"/> for a clean end-of-stream (the peer closed
    /// gracefully between requests), a wire-level failure (truncated line,
    /// malformed header, socket error), a configured-limit rejection
    /// (414 / 431 / 413, after emitting the status response), and a
    /// read-timeout (idle keep-alive or slow-header Slowloris, after
    /// emitting a 408 when mid-headers). The receive enumerable treats
    /// them all the same way: the connection is done.
    /// </summary>
    private async Task<Http1Context?> TryReadRequestAsync(CancellationToken cancellationToken)
    {
        using Http1ReadTimeout readTimeout = new(cancellationToken, _limits.KeepAliveTimeout, _limits.RequestHeadersTimeout);

        try
        {
            Http1Context? context = await Http1MessageReader.ReadRequestAsync(
                Stream,
                ConnectionInfo,
                GetScheme(),
                _limits,
                _interceptors,
                readTimeout,
                cancellationToken).ConfigureAwait(false);

            return context;
        }
        catch (Http1LimitExceededException rejection)
        {
            // RFC 9110 §15.5 — a request that violates a configured limit gets the matching
            // status response (414 / 431 / 413) before the connection is closed, rather than a
            // silent drop, so a conformant client learns why.
            await TryWriteErrorResponseAsync(rejection.StatusCode, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (HttpRequestRejectedException rejection)
        {
            // A request-parse interceptor rejected the request. Caught explicitly — ahead of the
            // wire-level classifier — so a rejection is always answered with its 4xx/5xx status
            // rather than being silently swallowed. The connection is not reused afterwards: the
            // request's remaining wire state is indeterminate, so keep-alive would desynchronize
            // the framing.
            await TryWriteErrorResponseAsync(rejection.StatusCode, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (readTimeout.TimedOut)
        {
            // An idle keep-alive or slow-header (Slowloris) peer exceeded its deadline. When we
            // were mid-headers, emit 408 Request Timeout (RFC 9110 §15.5.9) before closing;
            // an idle keep-alive connection (no request bytes yet) is simply reclaimed.
            if (readTimeout.IsHeadersPhase)
            {
                await TryWriteErrorResponseAsync(HttpStatusCode.RequestTimeout, cancellationToken).ConfigureAwait(false);
            }

            return null;
        }
        catch (Exception ex) when (IsWireLevelFailure(ex))
        {
            // Per-connection wire-level failure. Drop the connection
            // (the receive enumerable yields no more values; the calling
            // `await using` disposes the connection) and let the
            // listener keep accepting subsequent connections.
            return null;
        }
    }

    /// <summary>
    /// Best-effort write of a minimal error response to the connection stream. Any I/O failure is
    /// swallowed: the peer may already be gone, and the connection is being closed regardless.
    /// </summary>
    private async Task TryWriteErrorResponseAsync(HttpStatusCode statusCode, CancellationToken cancellationToken)
    {
        try
        {
            await Http1MessageWriter.WriteErrorResponseAsync(Stream, statusCode, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsWireLevelFailure(ex) || ex is OperationCanceledException)
        {
            // The response could not be delivered; the connection is dropped anyway.
        }
    }

    /// <summary>
    /// Classifies whether <paramref name="exception"/> represents a
    /// per-connection wire-level failure that should toss the connection
    /// rather than crash the host.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item><description><see cref="EndOfStreamException"/>: the peer
    ///   closed the socket mid-message (also covers the TLS-on-plain-HTTP
    ///   case where the line reader hits EOF before seeing CRLF).</description></item>
    ///   <item><description><see cref="IOException"/>: catch-all for
    ///   socket / stream I/O errors during the read.</description></item>
    ///   <item><description><see cref="SocketException"/>: lower-level
    ///   transport failures that escape <see cref="IOException"/> wrapping
    ///   on some runtimes.</description></item>
    ///   <item><description><see cref="InvalidDataException"/>: the
    ///   <see cref="Http1MessageReader"/> rejected the request line,
    ///   header block, or body framing as malformed.</description></item>
    /// </list>
    /// <para>
    /// Other exception types &#8211; <see cref="OperationCanceledException"/>,
    /// <see cref="ArgumentNullException"/>, <see cref="NullReferenceException"/>,
    /// and so on &#8211; propagate so cooperative shutdown signals and
    /// programmer errors are not silently swallowed.
    /// </para>
    /// </remarks>
    private static bool IsWireLevelFailure(Exception exception)
    {
        return exception is EndOfStreamException
            or IOException
            or SocketException
            or InvalidDataException;
    }
}
