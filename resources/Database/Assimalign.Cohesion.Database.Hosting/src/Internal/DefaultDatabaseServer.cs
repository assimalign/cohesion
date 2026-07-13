using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Security;

namespace Assimalign.Cohesion.Database.Hosting.Internal;

/// <summary>
/// The default server runtime: an accept loop over the composed listener, a
/// bounded session registry, and a two-phase stop (graceful drain within the
/// budget, then abort).
/// </summary>
internal sealed class DefaultDatabaseServer : IDatabaseServer
{
    private readonly DatabaseServerOptions _options;
    private readonly IReadOnlyList<IDatabaseEngine> _engines;
    private readonly IConnectionListener _listener;
    private readonly IDatabaseAuthenticator _authenticator;
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

    internal DefaultDatabaseServer(DatabaseServerOptions options)
    {
        _options = options;
        _engines = [.. options.Engines];
        _listener = options.Listener!;
        _authenticator = options.Authenticator ?? DatabaseAuthenticator.AllowAll;
    }

    /// <inheritdoc />
    public IReadOnlyList<IDatabaseEngine> Engines => _engines;

    /// <inheritdoc />
    public IReadOnlyCollection<IDatabaseServerSession> Sessions => _sessions.Values.ToArray();

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
                // The host disposed the listener out from under the server.
                break;
            }

            if (_sessions.Count >= _options.MaxSessions)
            {
                _ = RejectAsync(connection, hardAbort);
                continue;
            }

            var session = new DatabaseServerSession(this, connection, _options, _engines, _authenticator);

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
