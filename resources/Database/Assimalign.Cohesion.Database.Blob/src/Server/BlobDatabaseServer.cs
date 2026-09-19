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

/// <summary>Serves streamed objects and metadata through the Blob protocol over a generic listener.</summary>
/// <remarks>
/// The server owns its listener after startup is attempted; the composition root owns the engine.
/// Startup binds the listener and shutdown first drains active exchanges, then aborts at the
/// configured deadline. Stop is terminal. Each authenticated session binds to one database.
/// </remarks>
public sealed class BlobDatabaseServer : IDatabaseServer
{
    private readonly BlobDatabaseServerOptions _options;
    private readonly IConnectionListener _listener;
    private readonly IDatabaseAuthenticator _authenticator;
    private readonly BlobDatabaseServerContext _context;
    private readonly ConcurrentDictionary<Guid, BlobDatabaseServerSession> _sessions = new();
    private readonly ConcurrentDictionary<Guid, Task> _rejections = new();

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

    private BlobDatabaseServer(BlobDatabaseEngine engine, BlobDatabaseServerOptions options)
    {
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
        Engine = engine;
        _options = options;
        _listener = options.Listener;
        _authenticator = options.Authenticator ?? DatabaseAuthenticator.AllowAll;
        _context = new BlobDatabaseServerContext(this, engine);
    }

    /// <summary>
    /// Gets the Blob engine this server fronts (the typed counterpart of
    /// <see cref="IDatabaseServerContext.Engine"/>).
    /// </summary>
    public BlobDatabaseEngine Engine { get; }

    /// <inheritdoc />
    public IDatabaseServerContext Context => _context;

    /// <summary>
    /// Creates a Blob database server over the given engine and options. The server
    /// is inert until <see cref="StartAsync"/> is called.
    /// </summary>
    /// <param name="engine">The Blob engine the server fronts. The composition root owns and disposes the engine.</param>
    /// <param name="options">The composition options. Requires a configured <see cref="BlobDatabaseServerOptions.Listener"/>.</param>
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

            if (Engine.State != EngineState.Running)
            {
                throw new DatabaseException($"The Blob engine is {Engine.State} and cannot accept sessions.");
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

                        foreach (BlobDatabaseServerSession session in _sessions.Values)
                        {
                            session.Abort();
                        }
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

            if (_sessions.Count >= _options.MaxSessions || Engine.State != EngineState.Running)
            {
                Guid rejectionId = Guid.NewGuid();
                Task rejection = RejectAsync(connection, hardAbort);
                _rejections.TryAdd(rejectionId, rejection);
                _ = rejection.ContinueWith(completed => _rejections.TryRemove(rejectionId, out _),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                continue;
            }

            var session = new BlobDatabaseServerSession(this, connection, _options, Engine, _authenticator);

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
            await using var writer = ProtocolFraming.CreateWriter(stream, leaveOpen: true);
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
        _sessions.TryRemove(session.Id, out _);
    }
}
