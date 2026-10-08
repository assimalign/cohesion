using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Security;
using Assimalign.Cohesion.Database.KeyValuePair.Internal;

namespace Assimalign.Cohesion.Database.KeyValuePair;

/// <summary>
/// The key-value model's wire-protocol server: fronts one
/// <see cref="KeyValueDatabaseEngine"/> on the network — accept loop over the
/// composed listener, the session state machine and frame pump, the
/// authentication/idle/session-limit guardrails, and the two-phase graceful
/// drain — as a sealed leaf of the area root's <see cref="DatabaseServer"/> base.
/// </summary>
/// <remarks>
/// <para>
/// Servers are per-model, and the root base is the only area-wide requirement:
/// every model ships its own <see cref="DatabaseServer"/> leaf against
/// <c>Connections</c> and the protocol child root, carrying its <b>own copy</b> of
/// the server machinery (owner decision 2026-07-14, made with this second model's
/// extraction evidence in hand: model independence outweighs the duplication
/// cost — wire parity is held by the protocol contract and per-model E2Es, not
/// by shared code; see docs/DESIGN.md). The key-value command grammar
/// (<c>docs/COMMANDS.md</c>) travels the protocol's existing Execute message
/// (statement text + named tuple-codec parameters) into the root's text-execute
/// seam, and the model's result sets ride the generic ResultHeader/Row/Complete
/// framing — zero protocol changes. Model-specific wire surface (binary command
/// frames, if measurement ever demands them) grows here.
/// </para>
/// <para>
/// <b>The lifecycle is the base's</b> (concrete-types plan, phase 4, #1260): the server is
/// created inert; <see cref="DatabaseServer.StartAsync"/> binds the configured listener before
/// it begins accepting, and a bind that fails disposes the listener and leaves the server stopped
/// for good; <see cref="DatabaseServer.StopAsync"/> drains within
/// <see cref="KeyValueDatabaseServerOptions.ShutdownDrainTimeout"/>, then aborts and releases the
/// listener, and releases it as well for a server that never started. Stop is terminal:
/// restarting means composing a fresh server and listener. The composition root retains
/// ownership of the engine; the server owns the listener lifecycle once startup is attempted.
/// Compose one with <see cref="Create"/>, or through the
/// <c>engineBuilder.AddServer(factory)</c> builder verb.
/// </para>
/// </remarks>
public sealed class KeyValueDatabaseServer : DatabaseServer
{
    private readonly KeyValueDatabaseEngine _engine;
    private readonly KeyValueDatabaseServerOptions _options;
    private readonly IConnectionListener _listener;
    private readonly DatabaseAuthenticator _authenticator;
    private readonly ConcurrentDictionary<Guid, KeyValueDatabaseServerSession> _sessions = new();

    // Soft stop ends the accept loop and cancels idle/handshake reads so sessions
    // close at the next frame boundary; hard abort cancels in-flight executions
    // and tears connections down. StopCoreAsync escalates from the first to the
    // second when the drain budget lapses.
    private CancellationTokenSource? _softStopSource;
    private CancellationTokenSource? _hardAbortSource;
    private Task? _acceptTask;

    private KeyValueDatabaseServer(KeyValueDatabaseEngine engine, KeyValueDatabaseServerOptions options)
        : base(engine)
    {
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
    /// Gets the key-value engine this server fronts.
    /// </summary>
    public new KeyValueDatabaseEngine Engine => _engine;

    /// <inheritdoc />
    public override IReadOnlyCollection<DatabaseServerSession> Sessions => [.. _sessions.Values];

    /// <summary>
    /// Creates a key-value database server over the given engine and options. The
    /// server is inert until <see cref="DatabaseServer.StartAsync"/> is called.
    /// </summary>
    /// <param name="engine">The key-value engine the server fronts. The composition root owns and disposes the engine.</param>
    /// <param name="options">The composition options. Requires a configured <see cref="KeyValueDatabaseServerOptions.Listener"/>.</param>
    /// <returns>The server.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="engine"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when the options carry no listener or a non-positive session limit.</exception>
    public static KeyValueDatabaseServer Create(KeyValueDatabaseEngine engine, KeyValueDatabaseServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(options);

        return new KeyValueDatabaseServer(engine, options);
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
                    foreach (KeyValueDatabaseServerSession session in _sessions.Values)
                    {
                        session.Abort();
                        aborted++;
                    }

                    KeyValueDatabaseEventSource.Log.SessionsAborted(_engine, aborted, _options.ShutdownDrainTimeout);
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
                KeyValueDatabaseEventSource.Log.SessionRejected(_engine, KeyValueDatabaseEventSource.RejectReason.SessionLimit, activeSessions, _options.MaxSessions);
                _ = RejectAsync(connection, hardAbort);
                continue;
            }

            var session = new KeyValueDatabaseServerSession(this, connection, _options, _engine, _authenticator);

            // The server-session gauge follows the registry exactly: up once here, down once when
            // OnSessionCompleted removes the session.
            if (_sessions.TryAdd(session.Id, session))
            {
                KeyValueDatabaseEventSource.Log.SessionAccepted(_engine, session, activeSessions + 1);
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

    internal void OnSessionCompleted(KeyValueDatabaseServerSession session)
    {
        if (_sessions.TryRemove(session.Id, out _))
        {
            KeyValueDatabaseEventSource.Log.SessionClosed(session, session.CloseReason, session.AcceptedTimestamp);
        }
    }
}
