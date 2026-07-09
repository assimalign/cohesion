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
    private readonly IHttpExchangeInterceptor[] _interceptors;
    private readonly IHttpExchangeInterceptor[] _responseInterceptors;

    public Http1ConnectionContext(IConnection connection, bool isSecure, Http1ConnectionListenerOptions.Http1Limits limits, IHttpExchangeInterceptor[] interceptors, IHttpExchangeInterceptor[] responseInterceptors)
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

            // Expose the raw chunked response body sink and the exchange control to registered
            // response interceptors so feature packages (streaming / SSE, protocol upgrade / CONNECT
            // tunnelling, interim responses) can wrap them and install typed response features —
            // without this transport depending on any of those packages. Zero interceptors → buffered
            // fast path. An HTTP/1.1 exchange owns its whole connection, so its control offers the
            // full surface: interim (1xx) writes, the raw-stream takeover, and the exchange abort.
            if (_responseInterceptors.Length > 0)
            {
                context.RunResponseInterceptors(
                    _responseInterceptors,
                    new Http1ResponseBodyStream(Stream, context),
                    new Http1ExchangeControl(context, Stream));
            }

            yield return context;

            if (!context.KeepAlive)
            {
                yield break;
            }
        }
    }

    public override async ValueTask SendAsync(IHttpContext context, CancellationToken cancellationToken = default)
    {
        if (context is not Http1Context http1Context)
        {
            throw new InvalidOperationException("The supplied context does not belong to an HTTP/1.1 connection.");
        }

        // The connection was taken over (accepted protocol upgrade / CONNECT tunnel — the
        // exchange's directive is TakeOver): the transition response went straight to the
        // surrendered raw stream and the connection no longer speaks HTTP. Checked before the
        // sink branch so a misused streaming feature can never finalize chunked framing into the
        // tunnel (RFC 9110 §7.8 / §9.3.6).
        if (http1Context.ResponseFinalized)
        {
            return;
        }

        // The exchange was aborted (IHttpExchangeControl.Abort / IHttpContext.Cancel — the
        // directive is Abort). HTTP/1.1 has no per-exchange reset finer than the connection, so
        // no response is written and the keep-alive loop ends after this exchange.
        if (http1Context.CancelRequested)
        {
            http1Context.KeepAlive = false;
            return;
        }

        // If a response feature streamed to the raw sink, the head and body are already on the
        // wire (the BeforeResponseHead hooks fired at the sink's head commit); finalize (emit the
        // terminating zero-length chunk) rather than writing a second, buffered response.
        if (http1Context.ResponseBodySink is { HasStarted: true } sink)
        {
            await sink.CompleteAsync(cancellationToken).ConfigureAwait(false);
            await http1Context.InvokeAfterResponseAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        // The final response head is about to be committed on the buffered path — the last
        // mutation point. Fire the BeforeResponseHead lifecycle hooks, then re-read the directive
        // so a hook that aborted or took over the exchange is honored instead of writing the head.
        await http1Context.InvokeBeforeResponseHeadAsync(cancellationToken).ConfigureAwait(false);

        if (http1Context.ResponseFinalized)
        {
            return;
        }

        if (http1Context.CancelRequested)
        {
            http1Context.KeepAlive = false;
            return;
        }

        // A hook may itself have started the response through the raw sink (its head is then
        // already on the wire) — finalize that response rather than writing a second one.
        if (http1Context.ResponseBodySink is { HasStarted: true } hookStartedSink)
        {
            await hookStartedSink.CompleteAsync(cancellationToken).ConfigureAwait(false);
            await http1Context.InvokeAfterResponseAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        // Commit point: from here the final response is on the wire, so the exchange control's
        // probes must report the response as started (no more interim writes or takeover).
        http1Context.MarkFinalResponseStarted();
        await Http1MessageWriter.WriteResponseAsync(Stream, http1Context, cancellationToken).ConfigureAwait(false);
        await http1Context.InvokeAfterResponseAsync(cancellationToken).ConfigureAwait(false);
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
