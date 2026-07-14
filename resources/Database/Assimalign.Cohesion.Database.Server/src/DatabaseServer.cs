using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Security;

namespace Assimalign.Cohesion.Database.Server;

using Internal;

/// <summary>
/// The guided base for per-model database servers: the <b>proven common server
/// core</b> — accept loop over the composed listener, the session table, the
/// session state machine and frame pump, the authentication/idle/session-limit
/// guardrails, and the two-phase graceful drain — implementing the area root's
/// <see cref="IDatabaseServer"/> contract. A model package derives its server
/// from this base (<c>SqlDatabaseServer</c>, <c>KeyValueDatabaseServer</c>),
/// supplying its one engine and its options subtype; model-specific wire
/// behavior grows in the derived type when the protocol's model-specific
/// surface lands.
/// </summary>
/// <remarks>
/// <para>
/// <b>Extraction record (2026-07-14, the second model server):</b> this library
/// is the executed extraction the area DESIGN's decision log demanded — "on the
/// second model server, extract the then-proven common core, with evidence."
/// The machinery here is what building <c>KeyValueDatabaseServer</c> proved
/// genuinely common; see docs/DESIGN.md for the prediction-vs-evidence record
/// (the evidence exceeded the prediction: the execute pump proved common too,
/// because the second model rides the root's text-execute seam).
/// </para>
/// <para>
/// The server is created inert; <see cref="StartAsync"/> begins accepting,
/// <see cref="StopAsync"/> drains within
/// <see cref="DatabaseServerOptions.ShutdownDrainTimeout"/> then aborts, and
/// disposal stops the server. The composition root owns the listener and the
/// engine; the server only accepts from the one and dispatches to the other.
/// </para>
/// </remarks>
public abstract class DatabaseServer : IDatabaseServer
{
    private readonly IDatabaseEngine _engine;
    private readonly DatabaseServerOptions _options;
    private readonly IConnectionListener _listener;
    private readonly IDatabaseAuthenticator _authenticator;
    private readonly DatabaseServerContext _context;
    private readonly ConcurrentDictionary<Guid, DatabaseServerSession> _sessions = new();

    // Soft stop ends the accept loop and cancels idle/handshake reads so sessions
    // close at the next frame boundary; hard abort cancels in-flight executions
    // and tears connections down. StopAsync escalates from the first to the
    // second when the drain budget lapses.
    private CancellationTokenSource? _softStopSource;
    private CancellationTokenSource? _hardAbortSource;
    private Task? _acceptTask;
    private bool _isRunning;
    private bool _isDisposed;
    private readonly object _lifecycleGate = new();

    /// <summary>
    /// Initializes the server core over the one engine this server fronts.
    /// </summary>
    /// <param name="engine">The engine the server fronts. The composition root owns and disposes the engine.</param>
    /// <param name="options">The composition options. Requires a bound <see cref="DatabaseServerOptions.Listener"/>.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="engine"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when the options carry no listener or a non-positive session limit.</exception>
    protected DatabaseServer(IDatabaseEngine engine, DatabaseServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(options);

        if (options.Listener is null)
        {
            throw new ArgumentException("A bound connection listener is required.", nameof(options));
        }
        if (options.MaxSessions <= 0)
        {
            throw new ArgumentException("The session limit must be positive.", nameof(options));
        }

        _engine = engine;
        _options = options;
        _listener = options.Listener;
        _authenticator = options.Authenticator ?? DatabaseAuthenticator.AllowAll;
        _context = new DatabaseServerContext(this, engine);
    }

    /// <inheritdoc />
    public IDatabaseServerContext Context => _context;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_lifecycleGate)
        {
            if (_isRunning)
            {
                return Task.CompletedTask;
            }

            _softStopSource = new CancellationTokenSource();
            _hardAbortSource = new CancellationTokenSource();
            _isRunning = true;
            _acceptTask = AcceptLoopAsync(_softStopSource.Token, _hardAbortSource.Token);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task? acceptTask;

        lock (_lifecycleGate)
        {
            if (!_isRunning)
            {
                return;
            }

            _isRunning = false;
            acceptTask = _acceptTask;
        }

        _softStopSource!.Cancel();

        if (acceptTask is not null)
        {
            try
            {
                await acceptTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The accept loop observed the stop signal mid-accept.
            }
        }

        // Graceful drain: session pumps never fault (they own their errors), so
        // awaiting their completions cannot throw.
        Task drain = Task.WhenAll(_sessions.Values.Select(session => session.Completion).ToArray());
        Task lapsed = Task.Delay(_options.ShutdownDrainTimeout, cancellationToken);

        if (await Task.WhenAny(drain, lapsed).ConfigureAwait(false) != drain)
        {
            _hardAbortSource!.Cancel();

            foreach (DatabaseServerSession session in _sessions.Values)
            {
                session.Abort();
            }
        }

        await drain.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);

        _isDisposed = true;
        _softStopSource?.Dispose();
        _hardAbortSource?.Dispose();

        GC.SuppressFinalize(this);
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
                // The composition root disposed the listener out from under the server.
                break;
            }

            if (_sessions.Count >= _options.MaxSessions)
            {
                _ = RejectAsync(connection, hardAbort);
                continue;
            }

            var session = new DatabaseServerSession(this, connection, _options, _engine, _authenticator);

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

    internal void OnSessionCompleted(DatabaseServerSession session)
    {
        _sessions.TryRemove(session.Id, out _);
    }
}
