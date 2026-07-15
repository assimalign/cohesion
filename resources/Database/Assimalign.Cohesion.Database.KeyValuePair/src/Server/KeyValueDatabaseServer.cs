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
/// drain — implementing the area root's <see cref="IDatabaseServer"/> contract
/// directly.
/// </summary>
/// <remarks>
/// Servers are per-model, and the root contract is the only area-wide requirement:
/// every model ships its own <see cref="IDatabaseServer"/> implementation against
/// <c>Connections</c> and the protocol child root, carrying its <b>own copy</b> of
/// the server machinery (owner decision 2026-07-14, made with this second model's
/// extraction evidence in hand: model independence outweighs the duplication
/// cost — wire parity is held by the protocol contract and per-model E2Es, not
/// by shared code; see docs/DESIGN.md). The key-value command grammar
/// (<c>docs/COMMANDS.md</c>) travels the protocol's existing Execute message
/// (statement text + named tuple-codec parameters) into the root's text-execute
/// seam, and the model's result sets ride the generic ResultHeader/Row/Complete
/// framing — zero protocol changes. Model-specific wire surface (binary command
/// frames, if measurement ever demands them) grows here. The server is created
/// inert; <see cref="StartAsync"/> begins accepting, <see cref="StopAsync"/>
/// drains within <see cref="KeyValueDatabaseServerOptions.ShutdownDrainTimeout"/>
/// then aborts, and disposal stops the server. The composition root owns the
/// listener and the engine; the server only accepts from the one and dispatches
/// to the other. Compose one with <see cref="Create"/>, or through the
/// <c>AddKeyValueServer(...)</c> builder verb.
/// </remarks>
public sealed class KeyValueDatabaseServer : IDatabaseServer
{
    private readonly KeyValueDatabaseServerOptions _options;
    private readonly IConnectionListener _listener;
    private readonly IDatabaseAuthenticator _authenticator;
    private readonly KeyValueDatabaseServerContext _context;
    private readonly ConcurrentDictionary<Guid, KeyValueDatabaseServerSession> _sessions = new();

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

    private KeyValueDatabaseServer(KeyValueDatabaseEngine engine, KeyValueDatabaseServerOptions options)
    {
        if (options.Listener is null)
        {
            throw new ArgumentException("A bound connection listener is required.", nameof(options));
        }
        if (options.MaxSessions <= 0)
        {
            throw new ArgumentException("The session limit must be positive.", nameof(options));
        }

        Engine = engine;
        _options = options;
        _listener = options.Listener;
        _authenticator = options.Authenticator ?? DatabaseAuthenticator.AllowAll;
        _context = new KeyValueDatabaseServerContext(this, engine);
    }

    /// <summary>
    /// Gets the key-value engine this server fronts (the typed counterpart of
    /// <see cref="IDatabaseServerContext.Engine"/>).
    /// </summary>
    public KeyValueDatabaseEngine Engine { get; }

    /// <inheritdoc />
    public IDatabaseServerContext Context => _context;

    /// <summary>
    /// Creates a key-value database server over the given engine and options. The
    /// server is inert until <see cref="StartAsync"/> is called.
    /// </summary>
    /// <param name="engine">The key-value engine the server fronts. The composition root owns and disposes the engine.</param>
    /// <param name="options">The composition options. Requires a bound <see cref="KeyValueDatabaseServerOptions.Listener"/>.</param>
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

            foreach (KeyValueDatabaseServerSession session in _sessions.Values)
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

            var session = new KeyValueDatabaseServerSession(this, connection, _options, Engine, _authenticator);

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

    internal void OnSessionCompleted(KeyValueDatabaseServerSession session)
    {
        _sessions.TryRemove(session.Id, out _);
    }
}
