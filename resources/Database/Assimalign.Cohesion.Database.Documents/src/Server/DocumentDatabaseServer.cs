using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Security;
using Assimalign.Cohesion.Database.Documents.Internal;

namespace Assimalign.Cohesion.Database.Documents;

/// <summary>Serves OQL, document CRUD and collection operations for one database per connection.</summary>
/// <remarks>The composition root owns the engine. The server owns its listener, binds on StartAsync,
/// drains on StopAsync, and cannot restart. Every operation uses an automatic transaction;
/// transactions over the wire are unsupported. Options are copied at creation.</remarks>
public sealed class DocumentDatabaseServer : DatabaseServer
{
    private readonly DocumentDatabaseEngine _engine;
    private readonly DocumentDatabaseServerOptions _options;
    private readonly IDuplexConnectionListener _listener;
    private readonly DatabaseAuthenticator _authenticator;
    private readonly ConcurrentDictionary<Guid, DocumentDatabaseServerSession> _sessions = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _rejections = new();

    // Soft stop ends the accept loop and cancels idle/handshake reads so sessions
    // close at the next frame boundary; hard abort cancels in-flight executions
    // and tears connections down. StopCoreAsync escalates from the first to the
    // second when the drain budget lapses.
    private CancellationTokenSource? _softStopSource;
    private CancellationTokenSource? _hardAbortSource;
    private Task? _acceptTask;

    private DocumentDatabaseServer(DocumentDatabaseEngine engine, DocumentDatabaseServerOptions options)
        : base(engine)
    {
        // The server keeps a copy, checked here, as the engine keeps a copy of its options (B3 of
        // the engine extensibility design): the sessions read the limits and timeouts live.
        options = options.Snapshot();
        if (options.Listener is null)
        {
            throw new ArgumentException(ErrorMessages.ListenerMissing, nameof(options));
        }
        if (options.MaxSessions <= 0)
        {
            throw new ArgumentException(ErrorMessages.SessionLimitInvalid, nameof(options));
        }

        if (!ValidTimeout(options.AuthenticationTimeout) || !ValidTimeout(options.IdleTimeout) || !ValidTimeout(options.ShutdownDrainTimeout))
        {
            throw new ArgumentException(ErrorMessages.TimeoutInvalid, nameof(options));
        }

        _engine = engine;
        _options = options;
        _listener = options.Listener;
        _authenticator = options.Authenticator ?? DatabaseAuthenticator.AllowAll;
    }

    /// <summary>
    /// Gets the Document engine this server fronts.
    /// </summary>
    public new DocumentDatabaseEngine Engine => _engine;

    /// <summary>
    /// Gets the server's own copy of the options it was created with, as
    /// <see cref="DocumentDatabaseEngine.EngineOptions"/> exposes the engine's.
    /// </summary>
    internal DocumentDatabaseServerOptions ServerOptions => _options;

    /// <inheritdoc />
    public override IReadOnlyCollection<DatabaseServerSession> Sessions => [.. _sessions.Values];

    /// <summary>
    /// Creates a Document database server over the given engine and options. The server
    /// is inert until <see cref="DatabaseServer.StartAsync"/> is called.
    /// </summary>
    /// <param name="engine">The Document engine the server fronts. The composition root owns and disposes the engine.</param>
    /// <param name="options">
    /// The composition options. Requires a configured <see cref="DocumentDatabaseServerOptions.Listener"/>.
    /// The server keeps a copy, so a later change to this object does not reach it.
    /// </param>
    /// <returns>The server.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="engine"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when the options carry no listener or a non-positive session limit or an invalid timer duration.</exception>
    public static DocumentDatabaseServer Create(DocumentDatabaseEngine engine, DocumentDatabaseServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(options);

        return new DocumentDatabaseServer(engine, options);
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
                Task drain = Task.WhenAll(_sessions.Values.Select(session => session.Completion).Concat(_rejections.Values.Select(completion => completion.Task)).ToArray());
                Task lapsed = Task.Delay(_options.ShutdownDrainTimeout, cancellationToken);

                if (await Task.WhenAny(drain, lapsed).ConfigureAwait(false) != drain)
                {
                    _hardAbortSource!.Cancel();

                    int aborted = 0;
                    foreach (DocumentDatabaseServerSession session in _sessions.Values)
                    {
                        session.Abort();
                        aborted++;
                    }

                    DocumentDatabaseEventSource.Log.SessionsAborted(_engine, aborted, _options.ShutdownDrainTimeout);
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
                DocumentDatabaseEventSource.Log.SessionRejected(_engine, DocumentDatabaseEventSource.RejectReason.SessionLimit, activeSessions, _options.MaxSessions);
                Guid rejectionId = Guid.NewGuid();
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _rejections.TryAdd(rejectionId, completion);
                _ = RejectAsync(connection, rejectionId, completion, softStop);
                continue;
            }

            var session = new DocumentDatabaseServerSession(this, connection, _options, _engine, _authenticator);

            // The server-session gauge follows the registry exactly: up once here, down once when
            // OnSessionCompleted removes the session.
            if (_sessions.TryAdd(session.Id, session))
            {
                DocumentDatabaseEventSource.Log.SessionAccepted(_engine, session, activeSessions + 1);
            }

            session.Start(softStop, hardAbort);
        }
    }

    /// <summary>
    /// Rejects an over-limit connection with an <see cref="ProtocolErrorCode.Unavailable"/>
    /// error frame; the connection never becomes a session.
    /// </summary>
    private async Task RejectAsync(IDuplexConnection connection, Guid id, TaskCompletionSource completion, CancellationToken softStop)
    {
        try
        {
            var stream = connection.AsStream();
            await using var writer = ProtocolFrameWriter.Create(stream, leaveOpen: true);
            var error = new ProtocolErrorMessage(ProtocolErrorCode.Unavailable, ErrorMessages.SessionLimitReached);

            await writer.WriteFrameAsync(new ProtocolFrame(ProtocolMessageType.Error, error.Encode()), softStop).ConfigureAwait(false);
            await writer.FlushAsync(softStop).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Best effort: the peer may already be gone; rejection must never
            // take the accept loop down.
        }
        finally
        {
            try { await connection.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Rejected peers have no session; teardown must still finish the tracked work.
            }
            finally
            {
                completion.TrySetResult();
                _rejections.TryRemove(id, out _);
            }
        }
    }

    private static bool ValidTimeout(TimeSpan value)
        => value == Timeout.InfiniteTimeSpan || (value >= TimeSpan.Zero && value.TotalMilliseconds <= uint.MaxValue - 1);

    internal void OnSessionCompleted(DocumentDatabaseServerSession session)
    {
        if (_sessions.TryRemove(session.Id, out _))
        {
            DocumentDatabaseEventSource.Log.SessionClosed(session, session.CloseReason, session.AcceptedTimestamp);
        }
    }
}
