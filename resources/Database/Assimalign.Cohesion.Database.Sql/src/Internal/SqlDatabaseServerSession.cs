using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Security;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// One server-side session pump: drives the protocol state machine
/// (connected → startup → authenticating → ready ⇄ executing → terminated)
/// over a single connection and delegates statement execution to the bound
/// engine session's text-execute seam.
/// </summary>
/// <remarks>
/// An internal sealed leaf of the root <see cref="DatabaseServerSession"/> (concrete-types plan,
/// row 11): the base owns the identity, and the negotiated version and authenticated principal,
/// which the handshake records once each through the base's one-shot setters; the engine session
/// is re-exposed typed by a covariant override.
/// </remarks>
internal sealed class SqlDatabaseServerSession : DatabaseServerSession
{
    private readonly SqlDatabaseServer _server;
    private readonly IConnection _connection;
    private readonly SqlDatabaseServerOptions _options;
    private readonly SqlDatabaseEngine _engine;
    private readonly DatabaseAuthenticator _authenticator;
    private readonly CancellationTokenSource _lifetimeSource;
    private readonly long _acceptedTimestamp;

    private ProtocolFrameReader? _reader;
    private ProtocolFrameWriter? _writer;
    private SqlDatabaseSession? _databaseSession;
    private Task _completion = Task.CompletedTask;
    private string _closeReason = SqlDatabaseEventSource.CloseReason.Unknown;

    internal SqlDatabaseServerSession(
        SqlDatabaseServer server,
        IConnection connection,
        SqlDatabaseServerOptions options,
        SqlDatabaseEngine engine,
        DatabaseAuthenticator authenticator)
    {
        _server = server;
        _connection = connection;
        _options = options;
        _engine = engine;
        _authenticator = authenticator;
        _lifetimeSource = CancellationTokenSource.CreateLinkedTokenSource(connection.ConnectionClosed);
        _acceptedTimestamp = SqlDatabaseEventSource.Log.SessionTimestamp();
    }

    /// <inheritdoc />
    public override SqlDatabaseSession? DatabaseSession => _databaseSession;

    /// <summary>
    /// Gets the task that completes when the session pump has fully wound down.
    /// Never faults — the pump owns its errors.
    /// </summary>
    internal Task Completion => _completion;

    /// <summary>
    /// Gets why the session ended, for its <c>SessionClosed</c> event; the pump sets it on the
    /// path that ends it.
    /// </summary>
    internal string CloseReason => _closeReason;

    /// <summary>
    /// Gets the timestamp the session's <c>SessionClosed</c> duration starts from; zero when the
    /// event was disabled at accept.
    /// </summary>
    internal long AcceptedTimestamp => _acceptedTimestamp;

    internal void Start(CancellationToken softStop, CancellationToken hardAbort)
    {
        _completion = RunAsync(softStop, hardAbort);
    }

    /// <summary>
    /// Tears the session down immediately: cancels any in-flight execution and
    /// aborts the connection (the drain-budget escalation path).
    /// </summary>
    internal void Abort()
    {
        try
        {
            _lifetimeSource.Cancel();
            _connection.Abort(new ConnectionAbortedException("The server is shutting down."));
        }
        catch (ObjectDisposedException)
        {
            // The session already wound down; abort is a no-op.
        }
    }

    /// <inheritdoc />
    /// <remarks>Idempotent: aborting a session that already wound down is a no-op.</remarks>
    protected override async ValueTask DisposeAsyncCore()
    {
        Abort();
        await _completion.ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationToken softStop, CancellationToken hardAbort)
    {
        // Yield so Start returns immediately and registration completes before frames flow.
        await Task.Yield();

        CancellationTokenRegistration abortRegistration = hardAbort.Register(static state => ((SqlDatabaseServerSession)state!).Abort(), this);

        Stream stream = _connection.AsStream();
        await using var channel = new ProtocolChannel(stream, SqlProtocol.Family, leaveOpen: true);
        _reader = channel.Reader;
        _writer = channel.Writer;

        try
        {
            if (await HandshakeAsync(softStop).ConfigureAwait(false))
            {
                await ReadyLoopAsync(softStop).ConfigureAwait(false);
            }
        }
        catch (ProtocolException exception)
        {
            // Framing or message-order violation: report and terminate.
            _closeReason = SqlDatabaseEventSource.CloseReason.ProtocolViolation;
            SqlDatabaseEventSource.Log.SessionProtocolViolation(this, exception.Message);
            await TryWriteErrorAsync(ProtocolErrorCode.ProtocolViolation, exception.Message).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Aborted, connection closed, or stop signaled mid-frame.
            _closeReason = SqlDatabaseEventSource.CloseReason.Canceled;
        }
        catch (ConnectionAbortedException)
        {
            _closeReason = SqlDatabaseEventSource.CloseReason.ConnectionAborted;
        }
        catch (IOException)
        {
            // The transport failed under the pump; nothing to report to the peer.
            _closeReason = SqlDatabaseEventSource.CloseReason.TransportFailed;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The peer gets an internal error frame only; the event carries the failure.
            _closeReason = SqlDatabaseEventSource.CloseReason.Faulted;
            SqlDatabaseEventSource.Log.SessionFaulted(this, exception);
            await TryWriteErrorAsync(ProtocolErrorCode.Internal, "An unexpected server error terminated the session.").ConfigureAwait(false);
        }
        finally
        {
            // Detach from the abort signal before the lifetime source is disposed
            // so a late hard abort cannot race a disposed token source.
            await abortRegistration.DisposeAsync().ConfigureAwait(false);
            await CleanupAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs startup, version negotiation, database binding, and the authenticate
    /// exchange under the authentication timeout. Returns true when the session
    /// reached the ready state.
    /// </summary>
    private async Task<bool> HandshakeAsync(CancellationToken softStop)
    {
        using var handshakeSource = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeSource.Token, softStop);
        handshakeSource.CancelAfter(_options.AuthenticationTimeout);

        ProtocolFrame? frame;

        try
        {
            frame = await _reader!.ReadFrameAsync(handshakeSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!_lifetimeSource.IsCancellationRequested && !softStop.IsCancellationRequested)
        {
            // Authentication timeout: drop the unauthenticated connection.
            ReportHandshakeTimeout();
            return false;
        }

        if (frame is null)
        {
            _closeReason = SqlDatabaseEventSource.CloseReason.PeerClosed;
            return false; // The peer closed before starting up.
        }

        if (frame.Value.Type != ProtocolMessageType.Startup)
        {
            await RefuseHandshakeAsync(ProtocolErrorCode.ProtocolViolation, $"Expected a startup frame but received {frame.Value.Type}.", string.Empty, string.Empty).ConfigureAwait(false);
            return false;
        }

        ProtocolStartupMessage startup = ProtocolStartupMessage.Decode(frame.Value.Payload.Span);

        if (!ProtocolVersion.TryNegotiate(startup.Version, out var negotiatedVersion))
        {
            await RefuseHandshakeAsync(ProtocolErrorCode.UnsupportedVersion, $"Protocol major version {startup.Version.Major} is not supported; the server speaks {ProtocolVersion.Current}.", startup.Database, startup.Principal).ConfigureAwait(false);
            return false;
        }

        SetNegotiatedVersion(negotiatedVersion);

        SqlDatabase? database;

        try
        {
            database = await ResolveDatabaseAsync(startup.Database, handshakeSource.Token).ConfigureAwait(false);
        }
        catch (SqlDataStorageFormatException exception)
        {
            // The database exists but is on a data-storage format this engine
            // refuses (#1099). The refusal is engine-authored and actionable, so
            // the client gets it instead of an opaque internal error; any other
            // open failure still takes the internal-error path.
            await RefuseHandshakeAsync(ProtocolErrorCode.Unavailable, exception.Message, startup.Database, startup.Principal).ConfigureAwait(false);
            return false;
        }

        if (database is null)
        {
            await RefuseHandshakeAsync(ProtocolErrorCode.DatabaseNotFound, $"The server's engine has no database named '{startup.Database}'.", startup.Database, startup.Principal).ConfigureAwait(false);
            return false;
        }

        // Authenticate exchange. The MVP challenge carries no payload (trust
        // method); the client's response bytes are handed to the authenticator
        // as opaque evidence.
        await WriteFrameAsync(ProtocolMessageType.Authenticate, ReadOnlyMemory<byte>.Empty, handshakeSource.Token).ConfigureAwait(false);

        try
        {
            frame = await _reader.ReadFrameAsync(handshakeSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!_lifetimeSource.IsCancellationRequested && !softStop.IsCancellationRequested)
        {
            ReportHandshakeTimeout();
            return false;
        }

        if (frame is null)
        {
            _closeReason = SqlDatabaseEventSource.CloseReason.PeerClosed;
            return false;
        }

        if (frame.Value.Type != ProtocolMessageType.AuthenticateResponse)
        {
            await RefuseHandshakeAsync(ProtocolErrorCode.ProtocolViolation, $"Expected an authenticate response but received {frame.Value.Type}.", startup.Database, startup.Principal).ConfigureAwait(false);
            return false;
        }

        bool authenticated = await _authenticator.AuthenticateAsync(startup.Database, startup.Principal, frame.Value.Payload, handshakeSource.Token).ConfigureAwait(false);

        if (!authenticated)
        {
            await RefuseHandshakeAsync(ProtocolErrorCode.AuthenticationFailed, $"Authentication failed for principal '{startup.Principal}'.", startup.Database, startup.Principal).ConfigureAwait(false);
            return false;
        }

        try
        {
            _databaseSession = await database.CreateSessionAsync(handshakeSource.Token).ConfigureAwait(false);
        }
        catch (DatabaseOfflineException exception)
        {
            // The database went offline after a failed durable flush (#1243): every session is
            // refused, with the coded reason, until it is reopened.
            await RefuseHandshakeAsync(ProtocolErrorCode.Unavailable, exception.Message, startup.Database, startup.Principal).ConfigureAwait(false);
            return false;
        }

        SetAuthenticatedPrincipal(startup.Principal);

        await WriteFrameAsync(ProtocolMessageType.Ready, ReadOnlyMemory<byte>.Empty, handshakeSource.Token).ConfigureAwait(false);
        return true;
    }

    private async Task ReadyLoopAsync(CancellationToken softStop)
    {
        var rowWriter = new DatabaseKeyWriter();

        while (true)
        {
            ProtocolFrame? frame;

            using (var idleSource = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeSource.Token, softStop))
            {
                idleSource.CancelAfter(_options.IdleTimeout);

                try
                {
                    frame = await _reader!.ReadFrameAsync(idleSource.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (softStop.IsCancellationRequested && !_lifetimeSource.IsCancellationRequested)
                {
                    // Graceful drain: the session was idle at the frame boundary.
                    _closeReason = SqlDatabaseEventSource.CloseReason.Shutdown;
                    await TryWriteErrorAsync(ProtocolErrorCode.Unavailable, "The server is shutting down.").ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (!_lifetimeSource.IsCancellationRequested)
                {
                    // Idle timeout eviction.
                    _closeReason = SqlDatabaseEventSource.CloseReason.IdleTimeout;
                    await TryWriteErrorAsync(ProtocolErrorCode.Unavailable, "The session was closed after exceeding the idle timeout.").ConfigureAwait(false);
                    return;
                }
            }

            if (frame is null)
            {
                _closeReason = SqlDatabaseEventSource.CloseReason.PeerClosed;
                return; // The peer closed cleanly between frames.
            }

            switch (frame.Value.Type)
            {
                case (ProtocolMessageType)SqlProtocolMessageType.Execute:
                    // Executions run on the session lifetime token, not the soft-stop
                    // token: a drain lets in-flight statements finish.
                    await ExecuteAsync(frame.Value, rowWriter, _lifetimeSource.Token).ConfigureAwait(false);
                    break;

                case ProtocolMessageType.Ping:
                    await WriteFrameAsync(ProtocolMessageType.Pong, ReadOnlyMemory<byte>.Empty, _lifetimeSource.Token).ConfigureAwait(false);
                    break;

                case ProtocolMessageType.Terminate:
                    _closeReason = SqlDatabaseEventSource.CloseReason.Terminated;
                    return;

                default:
                    await RejectUnexpectedFrameAsync(frame.Value.Type).ConfigureAwait(false);
                    return;
            }
        }
    }

    private async Task ExecuteAsync(ProtocolFrame frame, DatabaseKeyWriter rowWriter, CancellationToken cancellationToken)
    {
        ProtocolExecuteMessage message = ProtocolExecuteMessage.Decode(frame.Payload.Span);
        Dictionary<string, object?>? parameters = null;

        if (message.Parameters.Count > 0)
        {
            parameters = new Dictionary<string, object?>(message.Parameters.Count);

            foreach ((string name, byte[] encoded) in message.Parameters)
            {
                try
                {
                    parameters[name] = DatabaseValueCodec.DecodeComponent(encoded);
                }
                catch (DatabaseTypeException exception)
                {
                    // A malformed parameter component is a wire violation, not a
                    // statement failure.
                    throw new ProtocolException($"Malformed component encoding for parameter '{name}'.", exception);
                }
            }
        }

        QueryResult result;

        try
        {
            result = await _databaseSession!.ExecuteAsync(message.Statement, parameters, cancellationToken).ConfigureAwait(false);
        }
        catch (DatabaseParseException exception)
        {
            // Statement-level failures keep the session in the ready state.
            await WriteErrorAsync(ProtocolErrorCode.ParseFailure, exception.Message, cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (DatabaseOfflineException exception)
        {
            // The database is offline (#1243): every statement is refused, with the coded
            // reason, until it is reopened; the session stays ready to report it.
            await WriteErrorAsync(ProtocolErrorCode.Unavailable, exception.Message, cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (DatabaseException exception)
        {
            await WriteErrorAsync(ProtocolErrorCode.ExecutionFailure, exception.Message, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (result is QueryResultSet resultSet)
        {
            await using (resultSet.ConfigureAwait(false))
            {
                var columns = new List<(string Name, byte Type)>(resultSet.Columns.Count);

                foreach (QueryColumn column in resultSet.Columns)
                {
                    columns.Add((column.Name, (byte)column.Type));
                }

                await WriteFrameAsync((ProtocolMessageType)SqlProtocolMessageType.ResultHeader, new ProtocolResultHeaderMessage(columns).Encode(), cancellationToken).ConfigureAwait(false);

                await foreach (QueryRow row in resultSet.GetRowsAsync(cancellationToken).ConfigureAwait(false))
                {
                    rowWriter.Reset();

                    for (int ordinal = 0; ordinal < row.FieldCount; ordinal++)
                    {
                        DatabaseValueCodec.Append(rowWriter, row.GetValue(ordinal));
                    }

                    await WriteFrameAsync((ProtocolMessageType)SqlProtocolMessageType.ResultRow, rowWriter.ToArray(), cancellationToken).ConfigureAwait(false);
                }

                // Evidence-driven fix kept from the (since-reversed) shared-core
                // extraction: ResultComplete carries the set's real AffectedCount
                // (SQL's materialized sets report -1, so SQL wire behavior is
                // unchanged; outcome sets elsewhere carry real counts).
                await WriteFrameAsync((ProtocolMessageType)SqlProtocolMessageType.ResultComplete, new ProtocolResultCompleteMessage(resultSet.AffectedCount).Encode(), cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        if (result.Status != QueryResultStatus.Success)
        {
            string detail = result.Diagnostics is { Count: > 0 } diagnostics && diagnostics[0].Message is { } diagnosticMessage
                ? $"{diagnostics[0].Code}: {diagnosticMessage}"
                : $"The statement completed with status {result.Status}.";

            await WriteErrorAsync(ProtocolErrorCode.ExecutionFailure, detail, cancellationToken).ConfigureAwait(false);
            return;
        }

        await WriteFrameAsync((ProtocolMessageType)SqlProtocolMessageType.ResultComplete, new ProtocolResultCompleteMessage(result.AffectedCount).Encode(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the startup-requested database on the server's one engine:
    /// already-open databases first, then an open attempt.
    /// </summary>
    /// <returns>The database, or <see langword="null"/> when the engine has none by that name.</returns>
    /// <exception cref="SqlDataStorageFormatException">The database is on a data-storage format the engine refuses.</exception>
    private async ValueTask<SqlDatabase?> ResolveDatabaseAsync(string name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (_engine.TryGetDatabase(name, out var open))
        {
            return open;
        }

        try
        {
            return await _engine.OpenDatabaseAsync(name, cancellationToken).ConfigureAwait(false);
        }
        catch (DatabaseNotFoundException)
        {
            // The engine has no database by that name.
        }

        return null;
    }

    private async ValueTask WriteFrameAsync(ProtocolMessageType type, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        await _writer!.WriteFrameAsync(new ProtocolFrame(type, payload), cancellationToken).ConfigureAwait(false);
        await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private ValueTask WriteErrorAsync(ProtocolErrorCode code, string message, CancellationToken cancellationToken)
        => WriteFrameAsync(ProtocolMessageType.Error, new ProtocolErrorMessage(code, message).Encode(), cancellationToken);

    /// <summary>
    /// Best-effort error write for teardown paths where the peer may already be gone.
    /// </summary>
    private async ValueTask TryWriteErrorAsync(ProtocolErrorCode code, string message)
    {
        try
        {
            await WriteErrorAsync(code, message, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
        }
    }

    /// <summary>
    /// Refuses the handshake: records why the session ends, writes the <c>HandshakeRefused</c>
    /// event, and sends the coded error frame (best effort).
    /// </summary>
    private ValueTask RefuseHandshakeAsync(ProtocolErrorCode code, string detail, string database, string principal)
    {
        _closeReason = SqlDatabaseEventSource.CloseReason.HandshakeRefused;
        SqlDatabaseEventSource.Log.HandshakeRefused(this, database, principal, code, detail);
        return TryWriteErrorAsync(code, detail);
    }

    /// <summary>
    /// Records that the handshake outlived the authentication timeout, which drops the connection
    /// without an error frame.
    /// </summary>
    private void ReportHandshakeTimeout()
    {
        _closeReason = SqlDatabaseEventSource.CloseReason.HandshakeTimedOut;
        SqlDatabaseEventSource.Log.HandshakeTimedOut(this, _options.AuthenticationTimeout);
    }

    /// <summary>
    /// Terminates the session on a frame the ready state does not accept: records the violation
    /// and sends the error frame (best effort).
    /// </summary>
    private ValueTask RejectUnexpectedFrameAsync(ProtocolMessageType type)
    {
        string violation = $"Unexpected {type} frame in the ready state.";
        _closeReason = SqlDatabaseEventSource.CloseReason.ProtocolViolation;
        SqlDatabaseEventSource.Log.SessionProtocolViolation(this, violation);
        return TryWriteErrorAsync(ProtocolErrorCode.ProtocolViolation, violation);
    }

    private async ValueTask CleanupAsync()
    {
        if (_databaseSession is not null)
        {
            try
            {
                await _databaseSession.DisposeAsync().ConfigureAwait(false);
            }
            catch (AggregateException exception)
            {
                // Session teardown must not mask the pump outcome. The root session reports every
                // teardown failure in one aggregate ("The session failed to close.").
                SqlDatabaseEventSource.Log.SessionCleanupFailed(this, exception);
            }
        }

        if (_reader is not null)
        {
            await _reader.DisposeAsync().ConfigureAwait(false);
        }

        if (_writer is not null)
        {
            await _writer.DisposeAsync().ConfigureAwait(false);
        }

        await _connection.DisposeAsync().ConfigureAwait(false);

        _lifetimeSource.Dispose();
        _server.OnSessionCompleted(this);
    }
}
