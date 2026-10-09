using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections.Internal;

internal sealed class Http1ConnectionContext : HttpStreamConnectionContext
{
    // The monotonic clock the request-body / response data-rate enforcement measures against.
    // TimeProvider.System in production; a plain field so a future test/composition seam can inject.
    private readonly TimeProvider _timeProvider = TimeProvider.System;
    private readonly Http1ConnectionListenerOptions.Http1Limits _limits;
    private readonly IHttpExchangeInterceptor[] _interceptors;
    private readonly IHttpExchangeInterceptor[] _responseInterceptors;
    private readonly string? _altSvcHeaderValue;

    // A graceful close (BeginGracefulClose) and the receive loop meet under this lock: the close marks
    // the exchange in flight and ends a read still waiting for the next request to begin, and the loop
    // registers each of those under the same lock, so a close that races a transition is never lost.
    private readonly Lock _gracefulCloseGate = new();
    private bool _gracefulCloseRequested;
    private Http1Context? _exchangeInFlight;
    private Http1ReadTimeout? _pendingRead;

    public Http1ConnectionContext(IConnection connection, bool isSecure, Http1ConnectionListenerOptions.Http1Limits limits, IHttpExchangeInterceptor[] interceptors, IHttpExchangeInterceptor[] responseInterceptors, string? altSvcHeaderValue)
        : base(connection, isSecure)
    {
        _limits = limits;
        _interceptors = interceptors;
        _responseInterceptors = responseInterceptors;
        _altSvcHeaderValue = altSvcHeaderValue;
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
    /// <para>
    /// A graceful close (<see cref="BeginGracefulClose"/>) ends the enumerable after the exchange in
    /// flight, whose keep-alive it clears, or at once when the connection is idle.
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
            // without this transport depending on any of those packages. Zero interceptors (none
            // registered for the response phase, none added to this exchange by a request hook) →
            // buffered fast path. An HTTP/1.1 exchange owns its whole connection, so its control
            // offers the full surface: interim (1xx) writes, the raw-stream takeover, and the
            // exchange abort.
            IHttpExchangeInterceptor[] responseInterceptors = context.ResolveResponseInterceptors(_responseInterceptors);

            if (responseInterceptors.Length > 0)
            {
                context.RunResponseInterceptors(
                    responseInterceptors,
                    new Http1ResponseBodyStream(Stream, context, _limits.MinResponseDataRate, _timeProvider, _altSvcHeaderValue),
                    new Http1ExchangeControl(context, Stream));
            }

            yield return context;

            if (!context.KeepAlive)
            {
                yield break;
            }

            // The body is streamed lazily and dispatched at head, so the application may have left
            // part (or all) of the request body unread. Consume it before reading the next request
            // so the connection realigns on the next request's framing; a drain that cannot complete
            // cleanly (slow trickle, over-cap, malformed body, wire failure) means the connection can
            // no longer be safely reused for keep-alive.
            if (!await context.DrainRequestBodyAsync(cancellationToken).ConfigureAwait(false))
            {
                yield break;
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// RFC 9112 §9.6. The exchange in flight, if any, stops being kept alive, so its response head
    /// carries <c>Connection: close</c> when it is committed and the receive loop ends after it. A read
    /// still waiting for the first octet of the next request ends now: the connection is idle, so it is
    /// reclaimed without a response, as on a keep-alive timeout. A request whose head has started to
    /// arrive is read and answered under its request-headers deadline, with <c>Connection: close</c>.
    /// No further request is read. A response head committed before the close began goes out without
    /// <c>Connection: close</c>; the connection still ends after that exchange, which RFC 9112 §9.5
    /// permits at any time.
    /// </remarks>
    public override void BeginGracefulClose()
    {
        lock (_gracefulCloseGate)
        {
            if (_gracefulCloseRequested)
            {
                return;
            }

            _gracefulCloseRequested = true;

            if (_exchangeInFlight is { } exchange)
            {
                exchange.KeepAlive = false;
            }

            _pendingRead?.CancelIdleWait();
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

        // The exchange was aborted (IHttpContext.Cancel / CancelAsync — the
        // directive is Abort). HTTP/1.1 has no per-exchange reset finer than the connection, so
        // no response is written and the keep-alive loop ends after this exchange.
        if (http1Context.CancelRequested)
        {
            http1Context.KeepAlive = false;
            return;
        }

        // RFC 9112 §5.1 / §7.1 — reading the body found the request malformed after its head was
        // dispatched: a broken chunk framing, or a trailer field line whose name is not a token. The
        // transport rejects the request itself, as HTTP/2 and HTTP/3 reset a malformed request's
        // stream: a 400 in place of whatever the application staged, then the connection closes,
        // since where the request ends on the wire is no longer known (#1333). A response already
        // on the wire is finished as it is, and the connection still closes after it.
        if (http1Context.IsRequestBodyMalformed)
        {
            http1Context.KeepAlive = false;

            if (!http1Context.HasFinalResponseStarted)
            {
                http1Context.MarkFinalResponseStarted();
                await TryWriteErrorResponseAsync(HttpStatusCode.BadRequest, cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        // If a response feature streamed to the raw sink, the head and body are already on the
        // wire (the BeforeResponseHead hooks fired at the sink's head commit, which also injected
        // the Alt-Svc advertisement); finalize (emit the terminating zero-length chunk) rather
        // than writing a second, buffered response.
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

        // Advertise the HTTP/3 endpoint on this buffered response unless the application (or a
        // lifecycle hook — they have all run by now) set its own Alt-Svc (RFC 7838 — the server
        // never overwrites an application value).
        HttpAltServiceInjector.Inject(http1Context.Response.Headers, _altSvcHeaderValue);

        // Commit point: from here the final response is on the wire, so the exchange control's
        // probes must report the response as started (no more interim writes or takeover).
        http1Context.MarkFinalResponseStarted();
        await Http1MessageWriter.WriteResponseAsync(Stream, http1Context, cancellationToken).ConfigureAwait(false);
        await http1Context.InvokeAfterResponseAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Attempts to read the next request head from the wire and dispatch it (its body is read
    /// lazily). Returns <see langword="null"/> for a clean end-of-stream (the peer closed gracefully
    /// between requests), a wire-level failure (truncated line, malformed header, malformed body
    /// framing, socket error), a head-limit rejection (414 / 431, after emitting the status
    /// response), and a read-timeout (idle keep-alive or slow-header Slowloris, after emitting a 408
    /// when mid-headers). The receive enumerable treats them all the same way: the connection is
    /// done. Body-size (413) and data-rate (408) violations surface after dispatch, on the streamed
    /// body read.
    /// </summary>
    private async Task<Http1Context?> TryReadRequestAsync(CancellationToken cancellationToken)
    {
        using Http1ReadTimeout readTimeout = new(cancellationToken, _limits.KeepAliveTimeout, _limits.RequestHeadersTimeout);

        // A graceful close that already began wants no next request; one that begins while this read
        // waits for it ends the wait (BeginGracefulClose).
        if (!TryBeginRead(readTimeout))
        {
            return null;
        }

        try
        {
            Http1Context? context = await Http1MessageReader.ReadRequestAsync(
                Stream,
                ConnectionInfo,
                GetScheme(),
                _limits,
                _interceptors,
                _timeProvider,
                readTimeout,
                cancellationToken).ConfigureAwait(false);

            if (context is not null)
            {
                AdmitExchange(context);
            }

            return context;
        }
        catch (Http1LimitExceededException rejection)
        {
            // RFC 9110 §15.5 — a request whose head violates a configured limit gets the matching
            // status response (414 / 431) before the connection is closed, rather than a silent
            // drop, so a conformant client learns why. Body-size (413) and data-rate (408)
            // violations surface after dispatch on the streamed body read, not here.
            await TryWriteErrorResponseAsync(rejection.StatusCode, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Http1BadRequestException)
        {
            // RFC 9112 §5.1 — a field line whose name is not a token (whitespace before the colon, an
            // empty name) MUST be answered with 400 before the connection is closed (#1333).
            await TryWriteErrorResponseAsync(HttpStatusCode.BadRequest, cancellationToken).ConfigureAwait(false);
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
        finally
        {
            EndRead(readTimeout);
        }
    }

    /// <summary>
    /// Registers <paramref name="readTimeout"/> as the read a graceful close ends while it waits for the
    /// next request, unless the close already began. The previous exchange is over by now, so a close
    /// no longer needs to mark it.
    /// </summary>
    /// <returns><see langword="false"/> when a graceful close began and no further request is read.</returns>
    private bool TryBeginRead(Http1ReadTimeout readTimeout)
    {
        lock (_gracefulCloseGate)
        {
            if (_gracefulCloseRequested)
            {
                return false;
            }

            _exchangeInFlight = null;
            _pendingRead = readTimeout;

            return true;
        }
    }

    /// <summary>
    /// Makes the exchange just read the one a graceful close marks. A close that began while its head
    /// was arriving marks it now, so its response still carries <c>Connection: close</c>.
    /// </summary>
    private void AdmitExchange(Http1Context context)
    {
        lock (_gracefulCloseGate)
        {
            _exchangeInFlight = context;

            if (_gracefulCloseRequested)
            {
                context.KeepAlive = false;
            }
        }
    }

    /// <summary>
    /// Unregisters <paramref name="readTimeout"/> before it is disposed, so a graceful close never
    /// cancels a disposed read.
    /// </summary>
    private void EndRead(Http1ReadTimeout readTimeout)
    {
        lock (_gracefulCloseGate)
        {
            if (ReferenceEquals(_pendingRead, readTimeout))
            {
                _pendingRead = null;
            }
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
