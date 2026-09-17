using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Security;
using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql;

/// <summary>
/// The SQL model's wire-protocol server: fronts one <see cref="SqlDatabaseEngine"/>
/// on the network — accept loop over the composed listener, the session state
/// machine and frame pump, the authentication/idle/session-limit guardrails, and
/// the two-phase graceful drain — implementing the area root's
/// <see cref="IDatabaseServer"/> contract directly.
/// </summary>
/// <remarks>
/// Servers are per-model, and the root contract is the only area-wide requirement:
/// every model ships its own <see cref="IDatabaseServer"/> implementation against
/// <c>Connections</c> and the protocol child root, carrying its <b>own copy</b> of
/// the server machinery (owner decision 2026-07-14, made with the second model's
/// extraction evidence in hand: model independence outweighs the duplication
/// cost — wire parity is held by the protocol contract and per-model E2Es, not
/// by shared code; see docs/DESIGN.md for the full placement history). This type
/// is where SQL-specific wire behavior grows
/// (typed relational payloads, SQL transaction frames) as the protocol's
/// model-specific surface lands; today execution rides the model-agnostic
/// text-execute seam on the root's <see cref="IDatabaseSession"/>. The server is
/// created inert; <see cref="StartAsync"/> binds the configured listener before
/// it begins accepting,
/// <see cref="StopAsync"/> drains within
/// <see cref="SqlDatabaseServerOptions.ShutdownDrainTimeout"/> then aborts, and
/// releases the listener. Stop is terminal: restarting means composing a fresh
/// server and listener. The composition root retains ownership of the engine;
/// the server owns the listener lifecycle once startup is attempted.
/// Compose one with <see cref="Create"/>, or through the <c>AddSqlServer(...)</c>
/// builder verb.
/// </remarks>
public sealed class SqlDatabaseServer : IDatabaseServer
{
    private readonly SqlDatabaseServerOptions _options;
    private readonly IConnectionListener _listener;
    private readonly IDatabaseAuthenticator _authenticator;
    private readonly SqlDatabaseServerContext _context;
    private readonly ConcurrentDictionary<Guid, SqlDatabaseServerSession> _sessions = new();

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

    private SqlDatabaseServer(SqlDatabaseEngine engine, SqlDatabaseServerOptions options)
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
        _context = new SqlDatabaseServerContext(this, engine);
    }

    /// <summary>
    /// Gets the SQL engine this server fronts (the typed counterpart of
    /// <see cref="IDatabaseServerContext.Engine"/>).
    /// </summary>
    public SqlDatabaseEngine Engine { get; }

    /// <inheritdoc />
    public IDatabaseServerContext Context => _context;

    /// <summary>
    /// Creates a SQL database server over the given engine and options. The server
    /// is inert until <see cref="StartAsync"/> is called.
    /// </summary>
    /// <param name="engine">The SQL engine the server fronts. The composition root owns and disposes the engine.</param>
    /// <param name="options">The composition options. Requires a configured <see cref="SqlDatabaseServerOptions.Listener"/>.</param>
    /// <returns>The server.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="engine"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when the options carry no listener or a non-positive session limit.</exception>
    public static SqlDatabaseServer Create(SqlDatabaseEngine engine, SqlDatabaseServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(options);

        return new SqlDatabaseServer(engine, options);
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

                        foreach (SqlDatabaseServerSession session in _sessions.Values)
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

            var session = new SqlDatabaseServerSession(this, connection, _options, Engine, _authenticator);

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

    internal void OnSessionCompleted(SqlDatabaseServerSession session)
    {
        _sessions.TryRemove(session.Id, out _);
    }
}
