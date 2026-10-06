using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Hosting.Internal;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Http.Connections;
using Assimalign.Cohesion.Hosting;

/// <summary>
/// The default <see cref="IWebApplicationServer"/>: a dedicated accept loop that dispatches every
/// accepted connection to its own tracked <see cref="Task"/>, and every stream of a multiplexed
/// (HTTP/2 or HTTP/3) connection to its own tracked <see cref="Task"/>.
/// </summary>
/// <remarks>
/// <para>
/// One <see cref="IHttpConnectionListener"/> feeds one accept loop. The loop never serves a
/// connection inline — it hands each accepted connection to <see cref="ServeConnectionAsync"/> and
/// immediately loops back to accept the next. That decoupling is the whole point of the rewrite: a
/// single idle HTTP/1.1 keep-alive client parked in its receive loop can no longer starve every
/// other connection, and a fault serving one connection can no longer stop the accept loop or crash
/// the process.
/// </para>
/// <para>
/// Within a connection, dispatch follows the protocol. An HTTP/1.1 connection carries one exchange
/// at a time and its transport reads the next request only when the receive loop asks for it, so the
/// loop serves each exchange inline and asks for the next only after the response is sent. HTTP/2
/// and HTTP/3 multiplex streams, so each exchange runs on its own task while the loop keeps
/// receiving; the transport's stream limits bound how many run at once.
/// </para>
/// <para>
/// Layering: wire-level failure isolation (truncated frames, peer reset, per-stream RST/GOAWAY)
/// already lives in <c>Assimalign.Cohesion.Http.Connections</c> — the receive enumerable simply
/// stops yielding on a wire error. This server owns only the concerns above that layer:
/// application-exception isolation per exchange, per-connection and per-stream dispatch,
/// connection/context/exchange disposal, in-flight tracking for the <see cref="StopAsync"/> drain,
/// and the optional concurrency cap. It does not re-implement any wire-protocol behaviour: it
/// finalizes every exchange through the connection context, which owns the wire encoding of a
/// response, a replacement <c>500</c>, and a reset alike.
/// </para>
/// </remarks>
internal sealed class WebApplicationServer : IWebApplicationServer, IHostService
{
    // Once a stop's budget has run out and the in-flight exchanges were cancelled, how long the stop
    // still waits for them to unwind before it releases the listener and completes.
    private static readonly TimeSpan _abortGracePeriod = TimeSpan.FromSeconds(1);

    private readonly IWebApplicationPipeline _pipeline;
    private readonly IHttpConnectionListener _listener;

    // The stop signals: Draining stops accepting and begins every connection's graceful close;
    // Aborted cancels what is still in flight once the stop's budget runs out.
    private readonly WebApplicationServerDrain _drain = new();

    // The server's own diagnostics: a bind or accept-loop failure, a connection fault, a drain the
    // budget cut short.
    private readonly WebApplicationServerLog _log;

    // The exchanges being served on every connection, reported when a stop's budget runs out.
    private int _exchangesInFlight;

    // In-flight per-connection tasks, keyed by a monotonic id so a completing connection can remove
    // exactly its own entry. ConcurrentDictionary because the accept loop adds while the connection
    // tasks (running on arbitrary pool threads) remove themselves.
    private readonly ConcurrentDictionary<long, Task> _connections = new();

    // Null == unlimited. Otherwise a fair gate around accept: a slot is acquired before accepting
    // and released when the connection it was acquired for finishes, bounding concurrent service.
    private readonly SemaphoreSlim? _connectionSlots;
    private readonly Lock _lifecycleLock = new();

    private long _connectionKey;
    private Task? _startTask;
    private Task? _stopTask;
    private Task? _acceptLoop;
    private int _stopped;

    public WebApplicationServer(WebApplicationServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _pipeline = options.Pipeline
            ?? throw new ArgumentException("A pipeline must be configured on the server options.", nameof(options));
        _listener = options.Listener
            ?? throw new ArgumentException("A listener must be configured on the server options.", nameof(options));
        _log = new WebApplicationServerLog(options.Logger);

        if (options.MaxConcurrentConnections is int limit)
        {
            if (limit <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    limit,
                    "The maximum concurrent connection count, when set, must be greater than zero.");
            }

            _connectionSlots = new SemaphoreSlim(limit, limit);
        }

        MaxConcurrentConnections = options.MaxConcurrentConnections;
    }

    /// <inheritdoc />
    public ServiceId Id { get; } = ServiceId.New();

    /// <summary>
    /// Gets the cap on concurrently served connections, or <see langword="null"/> when the server is
    /// unlimited.
    /// </summary>
    internal int? MaxConcurrentConnections { get; }

    /// <summary>
    /// Binds the configured listener and then starts the accept loop.
    /// </summary>
    /// <remarks>
    /// Per the host-service contract the loop runs as a stored <see cref="Task"/> — never an
    /// <c>async void</c> thread-pool work item — so its exceptions are observable rather than
    /// escalated to a process-terminating unhandled exception. Concurrent and repeated calls await
    /// the same bind operation.
    /// </remarks>
    /// <param name="cancellationToken">The cancellation token for listener binding.</param>
    /// <returns>A task that completes only after the listener is bound and the loop is scheduled.</returns>
    /// <exception cref="HostStartupException">Thrown when the configured listener cannot be bound.</exception>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_lifecycleLock)
        {
            // A stop already ran (or is running): the shutdown source is cancelled/disposed, so a
            // stray start is a no-op. In-process restart builds a fresh host and listener instance.
            if (Volatile.Read(ref _stopped) == 1)
            {
                return Task.CompletedTask;
            }

            return _startTask ??= StartCoreAsync(cancellationToken);
        }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource bindCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _drain.Draining);

        try
        {
            await _listener.BindAsync(bindCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (_drain.Draining.IsCancellationRequested)
        {
            // StopAsync won a race with startup. It awaits this task and owns terminal listener
            // release, so cancellation of the pending bind is a clean stop rather than a startup
            // failure.
            return;
        }
        catch (HostStartupException exception)
        {
            _log.BindFailed(_listener.Protocols, exception);
            throw;
        }
        catch (Exception exception)
        {
            _log.BindFailed(_listener.Protocols, exception);
            throw new HostStartupException(
                "The web application server failed to bind its configured listener.",
                exception);
        }

        lock (_lifecycleLock)
        {
            // Stop may have won a race with an asynchronous bind. In that case StopAsync owns
            // listener release and this start completes without touching its disposed token source.
            if (Volatile.Read(ref _stopped) == 1)
            {
                return;
            }

            CancellationToken drainingToken = _drain.Draining;
            _acceptLoop = Task.Run(() => AcceptLoopAsync(drainingToken));
        }
    }

    /// <summary>
    /// Drains the server lame-duck style and then disposes the listener: stops accepting, lets the
    /// exchanges in flight finish within the caller's budget, and cancels whatever outlives it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The drain begins at once (<see cref="WebApplicationServerDrain.Begin"/>): the accept loop stops,
    /// and every live connection begins its graceful close — it takes no new exchange and tells its
    /// peer (HTTP/1.1 <c>Connection: close</c>, an HTTP/2 or HTTP/3 <c>GOAWAY</c>), and an idle
    /// keep-alive connection closes at once. Nothing is cancelled: the exchanges already running finish
    /// and their responses are delivered. A connection task completes only after every exchange it
    /// dispatched has, and it swallows its own cancellation and faults, so the drain covers every
    /// in-flight exchange on every connection without surfacing an unobserved exception.
    /// </para>
    /// <para>
    /// When <paramref name="cancellationToken"/> fires first, the drain is aborted: every exchange
    /// still running is cancelled (<see cref="IHttpContext.RequestCancelled"/>), every connection still
    /// open is aborted, and the stop waits up to <see cref="_abortGracePeriod"/> for them to unwind
    /// before it releases the listener. The stop still completes normally then — the server is stopped;
    /// only the graceful part was cut short, which the caller knows from its own token. Repeated calls
    /// share the first call's task, and a stop before start is safe.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">The drain budget: when it fires, what is still in flight is cancelled.</param>
    /// <returns>A task that completes once the in-flight connections and exchanges have drained, or the budget ran out, and the listener is released.</returns>
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_lifecycleLock)
        {
            return _stopTask ??= StopCoreAsync(cancellationToken);
        }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref _stopped, 1);
        Exception? shutdownSignalFailure = null;
        long drainStarted = Stopwatch.GetTimestamp();

        try
        {
            try
            {
                _drain.Begin();
            }
            catch (Exception exception)
            {
                // Cancellation callbacks are user-extensible. Record their failure, but continue
                // through bind/accept unwinding, the drain, and listener release before surfacing it.
                shutdownSignalFailure = exception;
            }

            try
            {
                await DrainAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The budget ran out with work still in flight. The stop still completes normally once
                // the listener is released: its job is done, only the graceful part was cut short, and
                // the caller knows that from its own token. Throwing would replay the cancellation to
                // every later StopAsync caller, which shares this task.
                _log.DrainTimedOut(
                    _connections.Count,
                    Volatile.Read(ref _exchangesInFlight),
                    Stopwatch.GetElapsedTime(drainStarted));
                await AbortDrainAsync().ConfigureAwait(false);
            }

            if (shutdownSignalFailure is not null)
            {
                ExceptionDispatchInfo.Capture(shutdownSignalFailure).Throw();
            }
        }
        finally
        {
            // Releasing the listener is not optional when a drain budget expires: StopAsync does
            // not complete until the endpoint is free for a replacement host instance.
            try
            {
                await _listener.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _drain.Dispose();
                _connectionSlots?.Dispose();
            }
        }
    }

    /// <summary>
    /// Waits, within the budget, for startup, the accept loop, and every in-flight connection — and
    /// so every exchange — to finish.
    /// </summary>
    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        if (_startTask is not null)
        {
            try
            {
                await _startTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Startup itself was cancelled. Stop still owns terminal listener cleanup.
            }
            catch (HostStartupException)
            {
                // The bind failure was already delivered to the startup caller. Stop is the
                // rollback path and must release resources without reporting it a second time.
            }
        }

        // Wait for the accept loop to observe the drain first: once it has stopped, no new
        // connection task can be added, so the in-flight snapshot below is complete.
        if (_acceptLoop is not null)
        {
            await _acceptLoop.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        Task[] inFlight = _connections.Values.ToArray();
        if (inFlight.Length > 0)
        {
            // Every connection task is self-contained (it never rethrows) and completes only
            // after its own exchanges have, so WhenAll drains every in-flight exchange without
            // observing an exception.
            await Task.WhenAll(inFlight).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Ends a drain whose budget ran out: cancels every exchange still running, aborts every
    /// connection still open, and waits briefly for them to unwind.
    /// </summary>
    private async Task AbortDrainAsync()
    {
        try
        {
            _drain.Abort();
        }
        // Deviates from the repo "catch specific exceptions" rule per design decision: middleware
        // register callbacks on the token this cancels, so the failure is arbitrary user code. Every
        // callback still ran, and an exchange whose callback threw unwinds like the others.
        catch (Exception)
        {
        }

        // Unwinding is cooperative and bounded: an exchange that honors RequestCancelled normally
        // finishes within the grace period, and one that ignores it keeps running after the stop
        // completes.
        Task[] unwinding = _acceptLoop is null
            ? _connections.Values.ToArray()
            : [_acceptLoop, .. _connections.Values];

        try
        {
            await Task.WhenAll(unwinding).WaitAsync(_abortGracePeriod).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Gate around accept: reserve a slot before accepting so the listener's backlog
                // channel applies backpressure when the cap is reached. The slot is handed to the
                // connection task, which releases it when the connection finishes.
                if (_connectionSlots is not null)
                {
                    await _connectionSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                IHttpConnection connection;
                try
                {
                    connection = await _listener.AcceptOrListenAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // The slot was reserved for a connection we never accepted; return it so a
                    // transient accept failure does not permanently shrink the cap.
                    ReleaseConnectionSlot();
                    throw;
                }

                // A connection outlives the drain's start (it is closed gracefully, not cancelled), so it
                // is served under the abort token, not the accept loop's.
                long key = Interlocked.Increment(ref _connectionKey);
                Task serve = ServeConnectionAsync(connection, key, _drain.Aborted);

                // Register before checking completion: if the task already finished (its finally
                // removed nothing because the key was absent), the follow-up removal below keeps
                // the map from leaking a completed entry.
                _connections[key] = serve;
                if (serve.IsCompleted)
                {
                    _connections.TryRemove(key, out _);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The drain began: the server accepts nothing new.
        }
        catch (ObjectDisposedException)
        {
            // The listener was disposed concurrently with shutdown.
        }
        catch (Exception exception)
        {
            // A fatal accept-loop failure (e.g. the transport listener itself faulted) ends the
            // loop; already-accepted connections still drain through StopAsync. Swallowed so the
            // stored task completes rather than escalating as an unobserved exception — and logged,
            // because the server keeps running but accepts nothing more.
            _log.AcceptLoopFaulted(exception);
        }
    }

    private async Task ServeConnectionAsync(IHttpConnection connection, long key, CancellationToken cancellationToken)
    {
        try
        {
            await using (connection.ConfigureAwait(false))
            {
                IHttpConnectionContext context = await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

                // Lame-duck drain: the context begins its graceful close when the stop begins, and the
                // connection is aborted if the stop's budget runs out first. Held until the connection's
                // streams have drained, so an abort still reaches a connection whose receive loop ended.
                using WebApplicationServerDrain.Registration drainRegistration = _drain.Register(connection, context);

                // The connection's concurrently served streams. Created by the first multiplexed
                // exchange, so an HTTP/1.1 connection never allocates one.
                MultiplexedExchangeTracker? streams = null;

                // The version of the exchanges this connection carries, for its fault diagnostics.
                HttpVersion version = HttpVersion.Unknown;

                try
                {
                    await foreach (IHttpContext exchange in context.ReceiveAsync(cancellationToken).ConfigureAwait(false))
                    {
                        version = exchange.Version;

                        if (IsMultiplexed(exchange))
                        {
                            // One task per stream, started the moment the transport yields it; the
                            // loop goes straight back for the next. The server queues nothing of its
                            // own, so concurrency is bounded by what the transport admits: HTTP/2
                            // SETTINGS_MAX_CONCURRENT_STREAMS, HTTP/3 QUIC stream credit.
                            (streams ??= new MultiplexedExchangeTracker()).Start(
                                () => ServeExchangeAsync(context, exchange, multiplexed: true, cancellationToken));

                            continue;
                        }

                        // HTTP/1.1: the transport reads (and realigns on) the next request only when
                        // the loop asks for it, so the exchange is served inline and the next one is
                        // not requested until this one's response is sent and the exchange disposed.
                        if (!await ServeExchangeAsync(context, exchange, multiplexed: false, cancellationToken).ConfigureAwait(false))
                        {
                            // The exchange was reset; a sequential connection ends with it.
                            break;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // The stop's budget ran out and the drain was aborted, including a sequential
                    // exchange's send cut off by it — not a fault.
                }
                catch (Exception exception)
                {
                    // Connection-level isolation boundary. Application faults never reach it: each
                    // exchange isolates its own (ServeExchangeAsync). What remains is a receive-side
                    // failure the transport surfaced, or a sequential connection whose response could
                    // not be put on the wire; either way this connection cannot carry another request.
                    // Aborting signals the peer; the enclosing await using still disposes it. Catching
                    // Exception is the deliberate process-crash guard, mirroring the accept-loop
                    // isolation boundary in HttpConnectionListener.
                    _log.ConnectionFaulted(connection, context, version, exception, _drain.Aborted.IsCancellationRequested);
                    connection.Abort(exception);
                }
                finally
                {
                    // StopAsync and the connection slot both wait on this task, so it must not
                    // complete, and the context must not be torn down under a running stream, while
                    // any exchange dispatched from this connection is still in flight.
                    if (streams is not null)
                    {
                        await streams.WhenDrainedAsync().ConfigureAwait(false);
                    }

                    await DisposeContextAsync(context).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // OpenAsync was cancelled during shutdown, or connection disposal observed cancellation.
        }
        catch (Exception exception)
        {
            // Teardown faults (OpenAsync or connection disposal) must not escape as an unobserved
            // task exception; the connection is being discarded regardless.
            _log.ConnectionFaulted(connection, context: null, HttpVersion.Unknown, exception, _drain.Aborted.IsCancellationRequested);
        }
        finally
        {
            _connections.TryRemove(key, out _);
            ReleaseConnectionSlot();
        }
    }

    /// <summary>
    /// Serves one exchange to completion: the middleware pipeline, then exactly one finalization
    /// through the connection context — the application's response, a <c>500</c> that replaces it,
    /// or a reset — then the response-completion callbacks and the exchange's disposal.
    /// </summary>
    /// <remarks>
    /// This is the application-exception isolation boundary. Whatever the pipeline, its completion
    /// callbacks, or the exchange's own disposal throw costs this exchange and nothing else. The one
    /// failure that escapes is a sequential (HTTP/1.1) connection's failed send: its response
    /// framing on the wire is then unknown, so the caller must stop reading from the connection. A
    /// multiplexed exchange never throws; a failed send resets its own stream.
    /// </remarks>
    /// <param name="context">The connection context the exchange was received from.</param>
    /// <param name="exchange">The exchange to serve.</param>
    /// <param name="multiplexed">Whether the exchange is one stream of a multiplexed connection.</param>
    /// <param name="cancellationToken">The server's abort token, cancelled only when a stop's budget runs out.</param>
    /// <returns>
    /// <see langword="true"/> when a response was sent, so a sequential connection may carry the next
    /// request; <see langword="false"/> when the exchange was reset.
    /// </returns>
    private async Task<bool> ServeExchangeAsync(
        IHttpConnectionContext context,
        IHttpContext exchange,
        bool multiplexed,
        CancellationToken cancellationToken)
    {
        WebExchangeTelemetry telemetry = WebExchangeTelemetry.Start(exchange);
        Interlocked.Increment(ref _exchangesInFlight);

        try
        {
            ResponseCompletionFeature responseCompletion = new();
            ExchangeOutcome outcome = await ExecutePipelineAsync(exchange, responseCompletion, cancellationToken).ConfigureAwait(false);

            bool responded;

            try
            {
                responded = await FinalizeAsync(context, exchange, outcome, cancellationToken).ConfigureAwait(false);
            }
            // Deviates from the repo "catch specific exceptions" rule per design decision: on a
            // multiplexed connection a failed send belongs to this stream alone (a response body
            // that throws, a lifecycle hook that throws, a write cut off by an aborted drain). Resetting the
            // stream releases its concurrency slot and drain accounting while its siblings carry on.
            catch (Exception) when (multiplexed)
            {
                await TryResetAsync(context, exchange, cancellationToken).ConfigureAwait(false);

                return false;
            }

            telemetry.SetOutcome(responded, canceled: outcome == ExchangeOutcome.Cancelled);

            // Completion callbacks are "after this response is on the wire" work, so they run only
            // for the application's own response — never after a replacement 500 or a reset.
            if (outcome == ExchangeOutcome.Completed)
            {
                await RunCompletionCallbacksAsync(responseCompletion).ConfigureAwait(false);
            }

            return responded;
        }
        finally
        {
            telemetry.Stop();
            await DisposeExchangeAsync(exchange).ConfigureAwait(false);
            Interlocked.Decrement(ref _exchangesInFlight);
        }
    }

    private async Task<ExchangeOutcome> ExecutePipelineAsync(
        IHttpContext exchange,
        ResponseCompletionFeature responseCompletion,
        CancellationToken cancellationToken)
    {
        try
        {
            exchange.Features.Set(responseCompletion);

            await _pipeline.ExecuteAsync(exchange, cancellationToken).ConfigureAwait(false);

            return ExchangeOutcome.Completed;
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested || exchange.RequestCancelled.IsCancellationRequested)
        {
            // The stop's budget ran out, or the exchange itself was cancelled (a peer reset, a closed
            // connection, IHttpContext.Cancel): an abandoned exchange, not a fault.
            return ExchangeOutcome.Cancelled;
        }
        // Deviates from the repo "catch specific exceptions" rule per design decision: this is the
        // application-exception isolation boundary around arbitrary middleware (docs/DESIGN.md,
        // "Error model"). An operation cancelled for any other reason — an application timeout, say
        // — is a fault like any other.
        catch (Exception)
        {
            return ExchangeOutcome.Faulted;
        }
    }

    /// <summary>
    /// Ends the exchange on the wire exactly once: the application's response when the pipeline
    /// completed; a <c>500</c> in place of the staged response when the pipeline faulted before the
    /// response started; otherwise — a cancelled exchange, or a fault after the response started,
    /// when a replacement could only complete a truncated response as if it were whole — a reset.
    /// </summary>
    /// <returns><see langword="true"/> when a response was sent; <see langword="false"/> when the exchange was reset.</returns>
    private static async Task<bool> FinalizeAsync(
        IHttpConnectionContext context,
        IHttpContext exchange,
        ExchangeOutcome outcome,
        CancellationToken cancellationToken)
    {
        if (outcome == ExchangeOutcome.Completed
            || (outcome == ExchangeOutcome.Faulted && !exchange.HasResponseStarted && TryPrepareServerErrorResponse(exchange)))
        {
            await context.SendAsync(exchange, cancellationToken).ConfigureAwait(false);

            return true;
        }

        await ResetAsync(context, exchange, cancellationToken).ConfigureAwait(false);

        return false;
    }

    /// <summary>
    /// Replaces whatever response a faulted application staged with a bodyless
    /// <c>500 Internal Server Error</c>. Returns <see langword="false"/> when the response itself
    /// cannot be reshaped; the caller then resets the exchange instead.
    /// </summary>
    private static bool TryPrepareServerErrorResponse(IHttpContext exchange)
    {
        Stream staged;

        try
        {
            IHttpResponse response = exchange.Response;

            response.Headers.Clear();

            // Swap in a fresh body rather than truncating the staged one: a seekable body the
            // application supplied may be a file it owns, and truncating it would destroy data.
            staged = response.Body;
            response.Body = new MemoryStream();
            response.StatusCode = HttpStatusCode.InternalServerError;
        }
        // Deviates from the repo "catch specific exceptions" rule per design decision: the faulted
        // application may have left its response in any state; failing to reshape it degrades to a
        // reset of this exchange, never to a fault that escapes the exchange.
        catch (Exception)
        {
            return false;
        }

        // The exchange disposes whatever body it ends with, so the one it no longer holds is
        // released here.
        try
        {
            staged.Dispose();
        }
        // Deviates from the repo "catch specific exceptions" rule per design decision: the staged
        // body is application-supplied; its disposal failure must not cost the replacement response.
        catch (Exception)
        {
        }

        return true;
    }

    /// <summary>
    /// Resets the exchange: requests its cancellation, then lets the connection context put the
    /// version's reset on the wire (an HTTP/2 <c>RST_STREAM</c>, an HTTP/3 stream abort, or no
    /// response and a connection that ends after the exchange on HTTP/1.1).
    /// </summary>
    private static async Task ResetAsync(IHttpConnectionContext context, IHttpContext exchange, CancellationToken cancellationToken)
    {
        try
        {
            await exchange.CancelAsync().ConfigureAwait(false);
        }
        // Deviates from the repo "catch specific exceptions" rule per design decision: cancellation
        // callbacks on RequestCancelled are application code. Their failure must not stop the reset,
        // which the exchange recorded before running them.
        catch (Exception)
        {
        }

        await context.SendAsync(exchange, cancellationToken).ConfigureAwait(false);
    }

    private static async Task TryResetAsync(IHttpConnectionContext context, IHttpContext exchange, CancellationToken cancellationToken)
    {
        try
        {
            await ResetAsync(context, exchange, cancellationToken).ConfigureAwait(false);
        }
        // Deviates from the repo "catch specific exceptions" rule per design decision: nothing more
        // can be done for this stream. Connection teardown releases it, and its siblings must not
        // pay for it.
        catch (Exception)
        {
        }
    }

    private static async Task RunCompletionCallbacksAsync(ResponseCompletionFeature responseCompletion)
    {
        try
        {
            await responseCompletion.CompleteAsync().ConfigureAwait(false);
        }
        // Deviates from the repo "catch specific exceptions" rule per design decision: callbacks
        // are application code that runs after the response is already delivered, so their failure
        // is contained to the exchange and never costs the connection.
        catch (Exception)
        {
        }
    }

    private static async ValueTask DisposeExchangeAsync(IHttpContext exchange)
    {
        try
        {
            await exchange.DisposeAsync().ConfigureAwait(false);
        }
        // Deviates from the repo "catch specific exceptions" rule per design decision: the exchange's
        // features and bodies may be application-supplied, and its response has already been
        // finalized; a disposal fault is contained to the exchange.
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Whether the exchange is one stream of a multiplexed connection (HTTP/2 or HTTP/3), whose
    /// siblings the transport can receive and serve while it runs.
    /// </summary>
    private static bool IsMultiplexed(IHttpContext exchange)
    {
        return exchange.Version is HttpVersion.Http20 or HttpVersion.Http30;
    }

    private void ReleaseConnectionSlot()
    {
        try
        {
            _connectionSlots?.Release();
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _stopped) == 1)
        {
            // A caller's drain budget expired and terminal cleanup disposed the gate before an
            // application task that ignored cancellation returned. The server is already stopped;
            // releasing that retired slot has no observable purpose.
        }
    }

    private static async ValueTask DisposeContextAsync(IHttpConnectionContext context)
    {
        // IHttpConnectionContext is not IAsyncDisposable: a context is a projection over the
        // connection, and the connection releases the transport on its own disposal. The server
        // still disposes any context that DOES hold resources, so a stateful context is torn down
        // deterministically when its per-connection loop ends. The HTTP/2 context is one — its
        // disposal is the RFC 9113 §6.8 graceful close (GOAWAY, then the pump stops and the output
        // completes) — which is why ServeConnectionAsync drains the connection's streams first.
        // AOT-safe — a type test, no reflection.
        switch (context)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }

    /// <summary>
    /// How an exchange's pipeline ended, which decides how the exchange is finalized.
    /// </summary>
    private enum ExchangeOutcome
    {
        /// <summary>The pipeline returned; the application's response is sent as staged.</summary>
        Completed,

        /// <summary>The pipeline threw; the exchange is answered with a 500 or, once its response started, reset.</summary>
        Faulted,

        /// <summary>The exchange was abandoned (server shutdown or exchange cancellation); it is reset.</summary>
        Cancelled,
    }
}
