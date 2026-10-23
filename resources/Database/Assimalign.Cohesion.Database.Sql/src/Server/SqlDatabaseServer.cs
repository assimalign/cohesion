using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Pipelines;
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
/// the two-phase graceful drain — as a sealed leaf of the area root's
/// <see cref="DatabaseServer"/> base.
/// </summary>
/// <remarks>
/// <para>
/// Servers are per-model, and the root base is the only area-wide requirement:
/// every model ships its own <see cref="DatabaseServer"/> leaf against
/// <c>Connections</c> and the protocol child root, carrying its <b>own copy</b> of
/// the server machinery (owner decision 2026-07-14, made with the second model's
/// extraction evidence in hand: model independence outweighs the duplication
/// cost — wire parity is held by the protocol contract and per-model E2Es, not
/// by shared code; see docs/DESIGN.md for the full placement history). This type
/// is where SQL-specific wire behavior grows (typed relational payloads, SQL
/// transaction frames) as the protocol's model-specific surface lands; today
/// execution rides the model-agnostic text-execute seam of the root's
/// <see cref="DatabaseSession"/>.
/// </para>
/// <para>
/// <b>The lifecycle is the base's</b> (concrete-types plan, phase 4, #1260): the server is
/// created inert; <see cref="DatabaseServer.StartAsync"/> binds the configured listener before
/// it begins accepting, and a bind that fails disposes the listener and leaves the server stopped
/// for good; <see cref="DatabaseServer.StopAsync"/> drains within
/// <see cref="SqlDatabaseServerOptions.ShutdownDrainTimeout"/>, then aborts and releases the
/// listener, and releases it as well for a server that never started. Stop is terminal:
/// restarting means composing a fresh server and listener. The composition root retains
/// ownership of the engine; the server owns the listener lifecycle once startup is attempted.
/// Compose one with <see cref="Create"/>, or through the
/// <c>engineBuilder.AddServer(factory)</c> builder verb.
/// </para>
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("SqlDatabaseServer")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.SqlDatabaseServer.DebugView))]
public sealed class SqlDatabaseServer : DatabaseServer
{
    private readonly SqlDatabaseEngine _engine;
    private readonly SqlDatabaseServerOptions _options;
    private readonly IDuplexConnectionListener _listener;
    private readonly DatabaseAuthenticator _authenticator;
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _rejections = new();
    private readonly ConcurrentDictionary<Guid, SqlDatabaseServerSession> _sessions = new();

    // Soft stop ends the accept loop and cancels idle/handshake reads so sessions
    // close at the next frame boundary; hard abort cancels in-flight executions
    // and tears connections down. StopCoreAsync escalates from the first to the
    // second when the drain budget lapses.
    private CancellationTokenSource? _softStopSource;
    private CancellationTokenSource? _hardAbortSource;
    private Task? _acceptTask;

    private SqlDatabaseServer(SqlDatabaseEngine engine, SqlDatabaseServerOptions options)
        : base(engine)
    {
        // The server keeps a copy, checked here, as the engine keeps a copy of its options (B1 of
        // the engine extensibility design): the sessions read the limits live.
        options = options.Snapshot();
        options.ValidateFor(engine.Name);

        _engine = engine;
        _options = options;
        _listener = options.Listener!;
        _authenticator = options.Authenticator ?? DatabaseAuthenticator.AllowAll;
    }

    /// <summary>
    /// Gets the SQL engine this server fronts.
    /// </summary>
    public new SqlDatabaseEngine Engine => _engine;

    /// <summary>
    /// Gets the server's own copy of the options it was created with, as
    /// <see cref="SqlDatabaseEngine.EngineOptions"/> exposes the engine's.
    /// </summary>
    internal SqlDatabaseServerOptions ServerOptions => _options;

    /// <inheritdoc />
    public override IReadOnlyCollection<DatabaseServerSession> Sessions => [.. _sessions.Values];

    /// <summary>
    /// Creates a SQL database server over the given engine and options. The server
    /// is inert until <see cref="DatabaseServer.StartAsync"/> is called.
    /// </summary>
    /// <param name="engine">The SQL engine the server fronts. The composition root owns and disposes the engine.</param>
    /// <param name="options">
    /// The composition options. Requires a configured <see cref="SqlDatabaseServerOptions.Listener"/>.
    /// The server keeps a copy, so a later change to this object does not reach it.
    /// </param>
    /// <returns>The server.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="engine"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The drain timeout or peer-close linger is not positive and finite within the runtime timer range.</exception>
    /// <exception cref="ArgumentException">Thrown when the options carry no listener or a non-positive session limit.</exception>
    public static SqlDatabaseServer Create(SqlDatabaseEngine engine, SqlDatabaseServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(options);

        return new SqlDatabaseServer(engine, options);
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

                // Order: root lifecycle gate -> join accept -> join pumps and rejections.
                // Pumps and rejection cleanup never acquire the lifecycle gate.
                // Graceful drain: session pumps never fault (they own their
                // errors), so awaiting their completions cannot throw.
                Task drain = Task.WhenAll(_sessions.Values.Select(session => session.Completion).Concat(_rejections.Values.Select(completion => completion.Task)).ToArray());
                Task lapsed = Task.Delay(_options.ShutdownDrainTimeout, cancellationToken);

                if (await Task.WhenAny(drain, lapsed).ConfigureAwait(false) != drain)
                {
                    _hardAbortSource!.Cancel();

                    int aborted = 0;
                    foreach (SqlDatabaseServerSession session in _sessions.Values)
                    {
                        session.Abort();
                        aborted++;
                    }

                    SqlDatabaseEventSource.Log.SessionsAborted(_engine, aborted, _options.ShutdownDrainTimeout);
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
            IDuplexConnection connection;

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
                SqlDatabaseEventSource.Log.SessionRejected(_engine, SqlDatabaseEventSource.RejectReason.SessionLimit, activeSessions, _options.MaxSessions);
                Guid rejectionId = Guid.NewGuid();
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                // Register before starting I/O; stop joins these after the accept loop exits.
                _rejections.TryAdd(rejectionId, completion);
                _ = RejectAsync(connection, rejectionId, completion, hardAbort);
                continue;
            }

            var session = new SqlDatabaseServerSession(this, connection, _options, _engine, _authenticator);

            // The server-session gauge follows the registry exactly: up once here, down once when
            // OnSessionCompleted removes the session.
            if (_sessions.TryAdd(session.Id, session))
            {
                SqlDatabaseEventSource.Log.SessionAccepted(_engine, session, activeSessions + 1);
            }

            session.Start(softStop, hardAbort);
        }
    }

    /// <summary>
    /// Rejects an over-limit connection with an <see cref="ProtocolErrorCode.Unavailable"/>
    /// error frame; the connection never becomes a session.
    /// </summary>
    internal async Task RejectAsync(IDuplexConnection connection, Guid id, TaskCompletionSource completion, CancellationToken hardAbort)
    {
        CancellationTokenSource? deadline = null;
        CancellationTokenRegistration abort = default;
        int abortRequested = 0;
        void AbortConnection()
        {
            // Cancellation can resume cooperative I/O inline before this callback runs.
            // Claim once before entering the transport; cleanup also calls this on expiry.
            if (Interlocked.Exchange(ref abortRequested, 1) != 0) { return; }
            try { connection.Abort(); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
        }
        try
        {
            deadline = CancellationTokenSource.CreateLinkedTokenSource(hardAbort, connection.ConnectionClosed);
            abort = deadline.Token.Register(AbortConnection);
            deadline.CancelAfter(_options.ShutdownDrainTimeout);
            var stream = connection.AsStream();
            await using var writer = ProtocolFrameWriter.Create(stream, leaveOpen: true);
            var error = new ProtocolErrorMessage(ProtocolErrorCode.Unavailable, "The server is at its session limit.");

            await writer.WriteFrameAsync(new ProtocolFrame(ProtocolMessageType.Error, error.Encode()), deadline.Token).ConfigureAwait(false);
            await writer.FlushAsync(deadline.Token).ConfigureAwait(false);
            // Complete output before disposal can abort the send pump. One deadline covers
            // write, flush, completion and peer drain; its callback releases tokenless I/O.
            await connection.Output.CompleteAsync().ConfigureAwait(false);
            using var linger = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            linger.CancelAfter(_options.TerminalDrainLinger);
            while (true)
            {
                ReadResult result = await connection.Input.ReadAsync(linger.Token).ConfigureAwait(false);
                connection.Input.AdvanceTo(result.Buffer.End);
                if (result.IsCompleted || result.IsCanceled) { break; }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Best effort: the peer may already be gone; rejection never faults the accept loop.
        }
        finally
        {
            if (deadline?.IsCancellationRequested == true) { AbortConnection(); }
            // Join cancellation callbacks before releasing the transport they can abort.
            await abort.DisposeAsync().ConfigureAwait(false);
            deadline?.Dispose();
            try { await connection.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
            finally
            {
                completion.TrySetResult();
                _rejections.TryRemove(id, out _);
            }
        }
    }

    internal void OnSessionCompleted(SqlDatabaseServerSession session)
    {
        if (_sessions.TryRemove(session.Id, out _))
        {
            SqlDatabaseEventSource.Log.SessionClosed(session, session.CloseReason, session.AcceptedTimestamp);
        }
    }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.SqlDatabaseServer _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.SqlDatabaseServer value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Sql.SqlDatabaseEngine Engine => _target.Engine;
        public bool IsRunning => _target.IsRunning;
    }
}
