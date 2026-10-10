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
/// Serves database-scoped graph statements and matched paths over the shared database protocol,
/// as a sealed leaf of the area root's <see cref="DatabaseServer"/> base.
/// </summary>
/// <remarks>
/// <para>
/// Execute serves scalar MATCH projections, graph mutations, and the existing catalog SHOW
/// surface. ExecutePaths serves read-only MATCH projections of nodes, relationships, and paths.
/// Explicit wire transaction control remains unsupported; each statement uses the engine's
/// automatic transaction. The reserved Transaction frame is rejected as a protocol violation.
/// This model owns its server machinery, following the SQL and Key-Value server design.
/// </para>
/// <para>
/// <b>The lifecycle is the base's</b> (concrete-types plan, phase 4, #1260), the one this server
/// carried before: the server is created inert; <see cref="DatabaseServer.StartAsync"/> binds the
/// configured listener before it begins accepting, and a bind that fails disposes the listener and
/// leaves the server stopped for good; <see cref="DatabaseServer.StopAsync"/> drains sessions
/// within <see cref="GraphDatabaseServerOptions.ShutdownDrainTimeout"/> before aborting remaining
/// work and releasing the listener, and releases it as well for a server that never started. Stop
/// is terminal. The composition root retains ownership of the engine; the server owns the listener
/// after startup is attempted.
/// </para>
/// </remarks>
public sealed class GraphDatabaseServer : DatabaseServer
{
    private readonly GraphDatabaseEngine _engine;
    private readonly GraphDatabaseServerOptions _options;
    private readonly IConnectionListener _listener;
    private readonly DatabaseAuthenticator _authenticator;
    private readonly ConcurrentDictionary<Guid, GraphDatabaseServerSession> _sessions = new();

    // Soft stop ends the accept loop and cancels idle/handshake reads so sessions
    // close at the next frame boundary; hard abort cancels in-flight executions
    // and tears connections down. StopCoreAsync escalates from the first to the
    // second when the drain budget lapses.
    private CancellationTokenSource? _softStopSource;
    private CancellationTokenSource? _hardAbortSource;
    private Task? _acceptTask;

    private GraphDatabaseServer(GraphDatabaseEngine engine, GraphDatabaseServerOptions options)
        : base(engine)
    {
        // The server keeps a copy, checked here, as the engine keeps a copy of its options (B3 of
        // the engine extensibility design): the sessions read the limits and timeouts live.
        options = options.Snapshot();
        if (options.Listener is null)
        {
            throw new ArgumentException("A connection listener is required.", nameof(options));
        }
        if (options.MaxSessions <= 0)
        {
            throw new ArgumentException("The session limit must be positive.", nameof(options));
        }

        _engine = engine;
        _options = options;
        _listener = options.Listener;
        _authenticator = options.Authenticator ?? DatabaseAuthenticator.AllowAll;
    }

    /// <summary>
    /// Gets the Graph engine this server fronts.
    /// </summary>
    public new GraphDatabaseEngine Engine => _engine;

    /// <summary>
    /// Gets the server's own copy of the options it was created with, as
    /// <see cref="GraphDatabaseEngine.EngineOptions"/> exposes the engine's.
    /// </summary>
    internal GraphDatabaseServerOptions ServerOptions => _options;

    /// <inheritdoc />
    public override IReadOnlyCollection<DatabaseServerSession> Sessions => [.. _sessions.Values];

    /// <summary>
    /// Creates a Graph database server over the given engine and options. The server
    /// is inert until <see cref="DatabaseServer.StartAsync"/> is called.
    /// </summary>
    /// <param name="engine">The Graph engine the server fronts. The composition root owns and disposes the engine.</param>
    /// <param name="options">
    /// The composition options. Requires a configured <see cref="GraphDatabaseServerOptions.Listener"/>.
    /// The server keeps a copy, so a later change to this object does not reach it.
    /// </param>
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
    protected override async Task StartCoreAsync(CancellationToken cancellationToken)
    {
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
            // server's terminal listener cleanup before propagating. The base
            // leaves the server stopped, so a later stop has nothing to release.
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
        _acceptTask = AcceptLoopAsync(_softStopSource.Token, _hardAbortSource.Token);
    }

    /// <inheritdoc />
    protected override async Task StopCoreAsync(CancellationToken cancellationToken)
    {
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

                    int aborted = 0;
                    foreach (GraphDatabaseServerSession session in _sessions.Values)
                    {
                        session.Abort();
                        aborted++;
                    }

                    GraphDatabaseEventSource.Log.SessionsAborted(_engine, aborted, _options.ShutdownDrainTimeout);
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

            int activeSessions = _sessions.Count;
            if (activeSessions >= _options.MaxSessions)
            {
                GraphDatabaseEventSource.Log.SessionRejected(_engine, GraphDatabaseEventSource.RejectReason.SessionLimit, activeSessions, _options.MaxSessions);
                _ = RejectAsync(connection, hardAbort);
                continue;
            }

            var session = new GraphDatabaseServerSession(this, connection, _options, _engine, _authenticator);

            // The server-session gauge follows the registry exactly: up once here, down once when
            // OnSessionCompleted removes the session.
            if (_sessions.TryAdd(session.Id, session))
            {
                GraphDatabaseEventSource.Log.SessionAccepted(_engine, session, activeSessions + 1);
            }

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
            await using var writer = ProtocolFrameWriter.Create(stream, leaveOpen: true);
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
        if (_sessions.TryRemove(session.Id, out _))
        {
            GraphDatabaseEventSource.Log.SessionClosed(session, session.CloseReason, session.AcceptedTimestamp);
        }
    }
}
