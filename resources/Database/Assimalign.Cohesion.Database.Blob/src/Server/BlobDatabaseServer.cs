using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Security;
using Assimalign.Cohesion.Database.Blob.Internal;

namespace Assimalign.Cohesion.Database.Blob;

/// <summary>
/// Serves streamed objects and metadata through the Blob protocol over a generic listener, as a
/// sealed leaf of the area root's <see cref="DatabaseServer"/> base.
/// </summary>
/// <remarks>
/// <para>
/// The server owns its listener after startup is attempted; the composition root owns the engine.
/// Startup binds the listener and shutdown first drains active exchanges, then aborts at the
/// configured deadline. Each authenticated session binds to one database.
/// </para>
/// <para>
/// <b>A failing worker refuses its database alone</b> (owner decision 42 of 2026-10-07). While a
/// background worker of the engine holds a failure of one database
/// (<see cref="DatabaseEngine.HasFailingWorker"/>), the server refuses that database's handshakes
/// and exchanges with <see cref="ProtocolErrorCode.Unavailable"/> and a message led by
/// <c>COHDBB003</c>, and serves the engine's other databases; the refusal ends with the failure,
/// or turns into the offline refusal (<c>COHDBB002</c>) once the engine gives up on the database.
/// The server refuses its start, new connections, handshakes and exchanges for every database only
/// while the engine is disposed or has failed as a whole
/// (<see cref="DatabaseEngine.HasEngineWideFailure"/>). Before the decision it refused all of them
/// while the engine was not <see cref="EngineState.Running"/>, which a failure of any one database
/// made it.
/// </para>
/// <para>
/// <b>The lifecycle is the base's</b> (concrete-types plan, phase 4, #1260): the server is created
/// inert; <see cref="DatabaseServer.StartAsync"/> refuses to start while the engine is disposed or
/// has failed as a whole, and otherwise binds the configured listener before it begins
/// accepting; a refused start and a bind that fails both dispose the listener and leave the server
/// stopped for good, so a later start throws <see cref="ObjectDisposedException"/>;
/// <see cref="DatabaseServer.StopAsync"/> drains sessions within
/// <see cref="BlobDatabaseServerOptions.ShutdownDrainTimeout"/> before aborting remaining work and
/// releasing the listener, and releases it as well for a server that never started. Stop is
/// terminal. Before phase 4 a start refused for the engine's state left the server inert, so a
/// later start could retry; the base makes every failed start terminal (pending owner
/// confirmation, decision 31 of the plan).
/// </para>
/// </remarks>
public sealed class BlobDatabaseServer : DatabaseServer
{
    private readonly BlobDatabaseEngine _engine;
    private readonly BlobDatabaseServerOptions _options;
    private readonly IConnectionListener _listener;
    private readonly DatabaseAuthenticator _authenticator;
    private readonly ConcurrentDictionary<Guid, BlobDatabaseServerSession> _sessions = new();
    private readonly ConcurrentDictionary<Guid, Task> _rejections = new();

    // Soft stop ends the accept loop and cancels idle/handshake reads so sessions
    // close at the next frame boundary; hard abort cancels in-flight executions
    // and tears connections down. StopCoreAsync escalates from the first to the
    // second when the drain budget lapses.
    private CancellationTokenSource? _softStopSource;
    private CancellationTokenSource? _hardAbortSource;
    private Task? _acceptTask;

    private BlobDatabaseServer(BlobDatabaseEngine engine, BlobDatabaseServerOptions options)
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

        ValidateTimeout(options.AuthenticationTimeout, nameof(options.AuthenticationTimeout));
        ValidateTimeout(options.IdleTimeout, nameof(options.IdleTimeout));
        ValidateTimeout(options.ShutdownDrainTimeout, nameof(options.ShutdownDrainTimeout));
        _engine = engine;
        _options = options;
        _listener = options.Listener;
        _authenticator = options.Authenticator ?? DatabaseAuthenticator.AllowAll;
    }

    /// <summary>
    /// Gets the Blob engine this server fronts.
    /// </summary>
    public new BlobDatabaseEngine Engine => _engine;

    /// <summary>
    /// Gets the server's own copy of the options it was created with, as
    /// <see cref="BlobDatabaseEngine.EngineOptions"/> exposes the engine's.
    /// </summary>
    internal BlobDatabaseServerOptions ServerOptions => _options;

    /// <inheritdoc />
    public override IReadOnlyCollection<DatabaseServerSession> Sessions => [.. _sessions.Values];

    /// <summary>
    /// Creates a Blob database server over the given engine and options. The server
    /// is inert until <see cref="DatabaseServer.StartAsync"/> is called.
    /// </summary>
    /// <param name="engine">The Blob engine the server fronts. The composition root owns and disposes the engine.</param>
    /// <param name="options">
    /// The composition options. Requires a configured <see cref="BlobDatabaseServerOptions.Listener"/>.
    /// The server keeps a copy, so a later change to this object does not reach it.
    /// </param>
    /// <returns>The server.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="engine"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when the options carry no listener or a non-positive session limit.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when a timeout is neither positive and supported nor infinite.</exception>
    public static BlobDatabaseServer Create(BlobDatabaseEngine engine, BlobDatabaseServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(options);

        return new BlobDatabaseServer(engine, options);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Refuses with a <see cref="DatabaseException"/> while the engine is disposed or has failed as
    /// a whole (<see cref="DatabaseEngine.HasEngineWideFailure"/>), after it disposed the listener.
    /// A worker's failure of one database does not refuse the start (owner decision 42).
    /// </remarks>
    protected override async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        if (_engine.RefusesEveryDatabase(out var state))
        {
            // The base leaves a server whose start failed stopped for good, so a later stop has
            // nothing to release: the listener the server owns is released here, before the
            // refusal propagates (concrete-types plan, row 9).
            var refusal = new DatabaseException($"The Blob engine is {state} and cannot accept sessions.");
            await DisposeListenerAfterFailedStartAsync().ConfigureAwait(false);
            throw refusal;
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
            // server's terminal listener cleanup before propagating. The base
            // leaves the server stopped, so a later stop has nothing to release.
            softStopSource.Dispose();
            hardAbortSource.Dispose();
            await DisposeListenerAfterFailedStartAsync().ConfigureAwait(false);
            throw;
        }

        _softStopSource = softStopSource;
        _hardAbortSource = hardAbortSource;
        _acceptTask = AcceptLoopAsync(_softStopSource.Token, _hardAbortSource.Token);
    }

    /// <inheritdoc />
    /// <remarks>
    /// A failure of the accept loop is rethrown, after the accepted sessions and rejections were
    /// drained (or aborted at the deadline) and the listener released; the server is stopped
    /// either way.
    /// </remarks>
    protected override async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_acceptTask is not null)
            {
                _softStopSource!.Cancel();

                ExceptionDispatchInfo? acceptFailure = null;
                try
                {
                    await _acceptTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // The accept loop observed the stop signal mid-accept.
                }
                catch (Exception exception)
                {
                    // Preserve the listener failure, but accepted connections still belong
                    // to this server and must finish or be aborted before it is released.
                    acceptFailure = ExceptionDispatchInfo.Capture(exception);
                }

                // Graceful drain: session pumps never fault (they own their
                // errors), so awaiting their completions cannot throw.
                Task drain = Task.WhenAll(_sessions.Values.Select(session => session.Completion).Concat(_rejections.Values).ToArray());
                Task lapsed = Task.Delay(_options.ShutdownDrainTimeout, cancellationToken);

                if (await Task.WhenAny(drain, lapsed).ConfigureAwait(false) != drain)
                {
                    _hardAbortSource!.Cancel();

                    int aborted = 0;
                    foreach (BlobDatabaseServerSession session in _sessions.Values)
                    {
                        session.Abort();
                        aborted++;
                    }

                    BlobDatabaseEventSource.Log.SessionsAborted(_engine, aborted, _options.ShutdownDrainTimeout);
                }

                await drain.ConfigureAwait(false);
                acceptFailure?.Throw();
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

    // Releases the listener of a start that failed, which the base leaves stopped for good: the
    // start's own failure is what the caller sees, so a failure to release is swallowed.
    private async ValueTask DisposeListenerAfterFailedStartAsync()
    {
        try
        {
            await _listener.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Preserve the failure that made startup fail. Listener cleanup is still
            // attempted here and StopAsync remains idempotent after this terminal state.
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

            // A connection names its database only in its handshake, so only a refusal of every
            // database turns it away here; a failing database is refused at the handshake.
            int activeSessions = _sessions.Count;
            bool atLimit = activeSessions >= _options.MaxSessions;
            EngineState state = default;
            if (atLimit || _engine.RefusesEveryDatabase(out state))
            {
                if (atLimit)
                {
                    BlobDatabaseEventSource.Log.SessionRejected(_engine, BlobDatabaseEventSource.RejectReason.SessionLimit, activeSessions, _options.MaxSessions);
                }
                else
                {
                    BlobDatabaseEventSource.Log.EngineRefused(_engine, BlobDatabaseEventSource.RefusalPhase.Accept, state);
                    BlobDatabaseEventSource.Log.SessionRejected(_engine, BlobDatabaseEventSource.RejectReason.EngineRefused, activeSessions, _options.MaxSessions);
                }

                Guid rejectionId = Guid.NewGuid();
                Task rejection = RejectAsync(connection, hardAbort);
                _rejections.TryAdd(rejectionId, rejection);
                _ = rejection.ContinueWith(completed => _rejections.TryRemove(rejectionId, out _),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                continue;
            }

            var session = new BlobDatabaseServerSession(this, connection, _options, _engine, _authenticator);

            // The server-session gauge follows the registry exactly: up once here, down once when
            // OnSessionCompleted removes the session.
            if (_sessions.TryAdd(session.Id, session))
            {
                BlobDatabaseEventSource.Log.SessionAccepted(_engine, session, activeSessions + 1);
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
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(hardAbort);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using CancellationTokenRegistration abort = timeout.Token.Register(static state =>
        {
            try { ((IConnection)state!).Abort(new ConnectionAbortedException("The rejected connection is closing.")); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
        }, connection);
        try
        {
            var stream = connection.AsStream();
            await using var writer = ProtocolFrameWriter.Create(stream, leaveOpen: true);
            var error = new ProtocolErrorMessage(ProtocolErrorCode.Unavailable, "The server is unavailable or at its session limit.");

            await writer.WriteFrameAsync(new ProtocolFrame(ProtocolMessageType.Error, error.Encode()), timeout.Token).ConfigureAwait(false);
            await writer.FlushAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Best effort: the peer may already be gone; rejection must never
            // take the accept loop down.
        }
        finally
        {
            try { await connection.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
        }
    }

    private static void ValidateTimeout(TimeSpan value, string name)
    {
        if (value != Timeout.InfiniteTimeSpan && (value <= TimeSpan.Zero || value.TotalMilliseconds > uint.MaxValue - 1))
        {
            throw new ArgumentOutOfRangeException(name, "A timeout must be positive or infinite.");
        }
    }

    internal void OnSessionCompleted(BlobDatabaseServerSession session)
    {
        if (_sessions.TryRemove(session.Id, out _))
        {
            BlobDatabaseEventSource.Log.SessionClosed(session, session.CloseReason, session.AcceptedTimestamp);
        }
    }
}
