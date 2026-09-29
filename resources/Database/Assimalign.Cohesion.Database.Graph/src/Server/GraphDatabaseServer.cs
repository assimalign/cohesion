using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Security;
using Assimalign.Cohesion.Database.Graph.Internal;

namespace Assimalign.Cohesion.Database.Graph;

/// <summary>
/// Serves database-scoped graph statements and matched paths over the shared database protocol.
/// </summary>
/// <remarks>
/// Execute serves scalar MATCH projections, graph mutations, and the existing catalog SHOW
/// surface. ExecutePaths serves read-only MATCH projections of nodes, relationships, and paths.
/// Explicit wire transaction control remains unsupported; each statement uses the engine's
/// automatic transaction. The reserved Transaction frame is rejected as a protocol violation.
/// This model owns its server machinery, following the SQL and Key-Value server design.
/// <see cref="StartAsync"/> binds the configured listener; <see cref="StopAsync"/> drains
/// sessions within <see cref="GraphDatabaseServerOptions.ShutdownDrainTimeout"/> before
/// aborting remaining work and releasing the listener. Stop is terminal. The composition
/// root retains ownership of the engine; the server owns the listener after startup is attempted.
/// </remarks>
public sealed class GraphDatabaseServer : IDatabaseServer
{
    private readonly GraphDatabaseServerOptions _options;
    private readonly IConnectionListener _listener;
    private readonly IDatabaseAuthenticator _authenticator;
    private readonly GraphDatabaseServerContext _context;
    private readonly ConcurrentDictionary<Guid, GraphDatabaseServerSession> _sessions = new();

    // Soft stop ends the accept loop and cancels idle/handshake reads so sessions
    // close at the next frame boundary; hard abort cancels in-flight executions
    // and tears connections down. StopAsync escalates from the first to the
    // second when the drain budget lapses.
    private CancellationTokenSource? _softStopSource;
    private CancellationTokenSource? _hardAbortSource;
    private Task? _acceptTask;
    private bool _isRunning;
    private bool _isDisposed;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);

    private GraphDatabaseServer(GraphDatabaseEngine engine, GraphDatabaseServerOptions options)
    {
        if (options.Listener is null)
        {
            throw new ArgumentException("A connection listener is required.", nameof(options));
        }
        if (options.MaxSessions <= 0)
        {
            throw new ArgumentException("The session limit must be positive.", nameof(options));
        }

        Engine = engine;
        _options = options;
        _listener = options.Listener;
        _authenticator = options.Authenticator ?? DatabaseAuthenticator.AllowAll;
        _context = new GraphDatabaseServerContext(this, engine);
    }

    /// <summary>
    /// Gets the Graph engine this server fronts (the typed counterpart of
    /// <see cref="IDatabaseServerContext.Engine"/>).
    /// </summary>
    public GraphDatabaseEngine Engine { get; }

    /// <inheritdoc />
    public IDatabaseServerContext Context => _context;

    /// <summary>
    /// Creates a Graph database server over the given engine and options. The server
    /// is inert until <see cref="StartAsync"/> is called.
    /// </summary>
    /// <param name="engine">The Graph engine the server fronts. The composition root owns and disposes the engine.</param>
    /// <param name="options">The composition options. Requires a configured <see cref="GraphDatabaseServerOptions.Listener"/>.</param>
    /// <returns>The server.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="engine"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when the options carry no listener or a non-positive session limit.</exception>
    public static GraphDatabaseServer Create(GraphDatabaseEngine engine, GraphDatabaseServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(options);

        return new GraphDatabaseServer(engine, options);
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);

            if (_isRunning)
            {
                return;
            }

            var softStopSource = new CancellationTokenSource();
            var hardAbortSource = new CancellationTokenSource();

            try
            {
                await _listener.BindAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // This is an ownership boundary: every bind failure, including a
                // catastrophic one after endpoint acquisition, must attempt the
                // server's terminal listener cleanup before propagating.
                _isDisposed = true;
                softStopSource.Dispose();
                hardAbortSource.Dispose();
                try
                {
                    await _listener.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Preserve the bind failure that made startup fail. Listener cleanup is still
                    // attempted here and StopAsync remains idempotent after this terminal state.
                }

                throw;
            }

            _softStopSource = softStopSource;
            _hardAbortSource = hardAbortSource;
            _isRunning = true;
            _acceptTask = AcceptLoopAsync(_softStopSource.Token, _hardAbortSource.Token);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _isRunning = false;
            try
            {
                if (_acceptTask is not null)
                {
                    _softStopSource!.Cancel();

                    try
                    {
                        await _acceptTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // The accept loop observed the stop signal mid-accept.
                    }

                    // Graceful drain: session pumps never fault (they own their
                    // errors), so awaiting their completions cannot throw.
                    Task drain = Task.WhenAll(_sessions.Values.Select(session => session.Completion).ToArray());
                    Task lapsed = Task.Delay(_options.ShutdownDrainTimeout, cancellationToken);

                    if (await Task.WhenAny(drain, lapsed).ConfigureAwait(false) != drain)
                    {
                        _hardAbortSource!.Cancel();

                        foreach (GraphDatabaseServerSession session in _sessions.Values)
                        {
                            session.Abort();
                        }
                    }

                    await drain.ConfigureAwait(false);
                }
            }
            finally
            {
                // Listener release follows accept-loop and session drain so no
                // live server work can race the terminal transport disposal.
                try
                {
                    await _listener.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    _softStopSource?.Dispose();
                    _hardAbortSource?.Dispose();
                    _softStopSource = null;
                    _hardAbortSource = null;
                    _acceptTask = null;
                }
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// A point-in-time snapshot of the sessions currently active on the server,
    /// for the server context.
    /// </summary>
    internal IReadOnlyCollection<IDatabaseServerSession> GetSessionsSnapshot()
        => _sessions.Values.ToArray();

    private async Task AcceptLoopAsync(CancellationToken softStop, CancellationToken hardAbort)
    {
        while (!softStop.IsCancellationRequested)
        {
            IConnection connection;

            try
            {
                connection = await _listener.AcceptAsync(softStop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ConnectionAbortedException)
            {
                // The listener was released while shutdown was ending the accept loop.
                break;
            }

            if (_sessions.Count >= _options.MaxSessions)
            {
                _ = RejectAsync(connection, hardAbort);
                continue;
            }

            var session = new GraphDatabaseServerSession(this, connection, _options, Engine, _authenticator);

            _sessions.TryAdd(session.Id, session);
            session.Start(softStop, hardAbort);
        }
    }

    /// <summary>
    /// Rejects an over-limit connection with an <see cref="ProtocolErrorCode.Unavailable"/>
    /// error frame; the connection never becomes a session.
    /// </summary>
    private static async Task RejectAsync(IConnection connection, CancellationToken hardAbort)
    {
        try
        {
            var stream = connection.AsStream();
            await using var writer = ProtocolFraming.CreateWriter(stream, leaveOpen: true);
            var error = new ProtocolErrorMessage(ProtocolErrorCode.Unavailable, "The server is at its session limit.");

            await writer.WriteFrameAsync(new ProtocolFrame(ProtocolMessageType.Error, error.Encode()), hardAbort).ConfigureAwait(false);
            await writer.FlushAsync(hardAbort).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Best effort: the peer may already be gone; rejection must never
            // take the accept loop down.
        }
        finally
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal void OnSessionCompleted(GraphDatabaseServerSession session)
    {
        _sessions.TryRemove(session.Id, out _);
    }
}
