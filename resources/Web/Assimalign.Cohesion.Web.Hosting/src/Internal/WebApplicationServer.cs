using System;
using System.Collections.Concurrent;
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
/// accepted connection to its own tracked <see cref="Task"/>.
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
/// Layering: wire-level failure isolation (truncated frames, peer reset, per-stream RST/GOAWAY)
/// already lives in <c>Assimalign.Cohesion.Http.Connections</c> — the receive enumerable simply
/// stops yielding on a wire error. This server owns only the concerns above that layer:
/// application-exception isolation, per-connection dispatch, connection/context disposal, in-flight
/// tracking for the <see cref="StopAsync"/> drain, and the optional concurrency cap. It does not
/// re-implement any wire-protocol behaviour.
/// </para>
/// </remarks>
internal sealed class WebApplicationServer : IWebApplicationServer, IHostService
{
    private readonly IWebApplicationPipeline _pipeline;
    private readonly IHttpConnectionListener _listener;
    private readonly CancellationTokenSource _shutdown = new();

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
    }

    /// <inheritdoc />
    public ServiceId Id { get; } = ServiceId.New();

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
            _shutdown.Token);

        try
        {
            await _listener.BindAsync(bindCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // StopAsync won a race with startup. It awaits this task and owns terminal listener
            // release, so cancellation of the pending bind is a clean stop rather than a startup
            // failure.
            return;
        }
        catch (HostStartupException)
        {
            throw;
        }
        catch (Exception exception)
        {
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

            // Capture the token on this thread so the scheduled loop never reads a disposed source.
            CancellationToken shutdownToken = _shutdown.Token;
            _acceptLoop = Task.Run(() => AcceptLoopAsync(shutdownToken));
        }
    }

    /// <summary>
    /// Signals shutdown, drains the in-flight connections, and disposes the listener.
    /// </summary>
    /// <remarks>
    /// Cancelling <see cref="_shutdown"/> stops the accept loop and unblocks every in-flight
    /// connection (an idle keep-alive parked in its receive loop observes the cancellation and
    /// unwinds). Each connection task swallows its own cancellation and faults, so the drain
    /// completes without surfacing an unobserved <see cref="OperationCanceledException"/>. Repeated
    /// calls, and a stop before start, are safe.
    /// </remarks>
    /// <param name="cancellationToken">The cancellation token that bounds waits during the drain.</param>
    /// <returns>A task that completes when the accept loop and all in-flight connections have drained.</returns>
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

        try
        {
            try
            {
                _shutdown.Cancel();
            }
            catch (Exception exception)
            {
                // Cancellation callbacks are user-extensible. Record their failure, but continue
                // through bind/accept unwinding and listener release before surfacing it.
                shutdownSignalFailure = exception;
            }

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

            // Wait for the accept loop to observe cancellation first: once it has stopped, no new
            // connection task can be added, so the in-flight snapshot below is complete.
            if (_acceptLoop is not null)
            {
                await _acceptLoop.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            Task[] inFlight = _connections.Values.ToArray();
            if (inFlight.Length > 0)
            {
                // Every connection task is self-contained (it never rethrows), so WhenAll drains
                // them without observing an exception.
                await Task.WhenAll(inFlight).WaitAsync(cancellationToken).ConfigureAwait(false);
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
                _shutdown.Dispose();
                _connectionSlots?.Dispose();
            }
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

                long key = Interlocked.Increment(ref _connectionKey);
                Task serve = ServeConnectionAsync(connection, key, cancellationToken);

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
            // Graceful shutdown requested through the shutdown token.
        }
        catch (ObjectDisposedException)
        {
            // The listener was disposed concurrently with shutdown.
        }
        catch (Exception)
        {
            // A fatal accept-loop failure (e.g. the transport listener itself faulted) ends the
            // loop; already-accepted connections still drain through StopAsync. Swallowed so the
            // stored task completes rather than escalating as an unobserved exception.
        }
    }

    private async Task ServeConnectionAsync(IHttpConnection connection, long key, CancellationToken cancellationToken)
    {
        try
        {
            await using (connection.ConfigureAwait(false))
            {
                IHttpConnectionContext context = await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

                try
                {
                    await foreach (IHttpContext exchange in context.ReceiveAsync(cancellationToken).ConfigureAwait(false))
                    {
                        try
                        {
                            await _pipeline.ExecuteAsync(exchange, cancellationToken).ConfigureAwait(false);
                            await context.SendAsync(exchange, cancellationToken).ConfigureAwait(false);
                        }
                        finally
                        {
                            await exchange.DisposeAsync().ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Cooperative shutdown or a per-exchange cancellation — a clean drain, not a fault.
                }
                catch (Exception exception)
                {
                    // Application-exception isolation boundary. A fault from the middleware pipeline
                    // (or the per-exchange receive/send) must tear down only THIS connection — never
                    // the accept loop, never the process. Aborting signals the peer the connection is
                    // dead; the enclosing await using still disposes it. Catching Exception here is
                    // the deliberate process-crash guard the rewrite exists to add, mirroring the
                    // accept-loop isolation boundary in HttpConnectionListener.
                    connection.Abort(exception);
                }
                finally
                {
                    await DisposeContextAsync(context).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // OpenAsync was cancelled during shutdown, or connection disposal observed cancellation.
        }
        catch (Exception)
        {
            // Teardown faults (OpenAsync or connection disposal) must not escape as an unobserved
            // task exception; the connection is being discarded regardless.
        }
        finally
        {
            _connections.TryRemove(key, out _);
            ReleaseConnectionSlot();
        }
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
        // IHttpConnectionContext is not IAsyncDisposable today: a context is a projection over the
        // connection, and the connection releases the transport on its own disposal. The server
        // still disposes any context that DOES hold resources, so a future stateful context is torn
        // down deterministically when its per-connection loop ends. AOT-safe — a type test, no
        // reflection.
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
}
