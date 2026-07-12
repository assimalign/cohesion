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

namespace Assimalign.Cohesion.Database.Server;

/// <summary>
/// One server-side session pump: drives the protocol state machine
/// (connected → startup → authenticating → ready ⇄ executing → terminated)
/// over a single connection and delegates statement execution to the bound
/// engine session's text-execute seam.
/// </summary>
internal sealed class DatabaseServerSession : IDatabaseServerSession
{
    private readonly DefaultDatabaseServer _server;
    private readonly IConnection _connection;
    private readonly DatabaseServerOptions _options;
    private readonly IReadOnlyList<IDatabaseEngine> _engines;
    private readonly IDatabaseAuthenticator _authenticator;
    private readonly CancellationTokenSource _lifetimeSource;

    private IProtocolFrameReader? _reader;
    private IProtocolFrameWriter? _writer;
    private IDatabaseSession? _databaseSession;
    private Task _completion = Task.CompletedTask;

    internal DatabaseServerSession(
        DefaultDatabaseServer server,
        IConnection connection,
        DatabaseServerOptions options,
        IReadOnlyList<IDatabaseEngine> engines,
        IDatabaseAuthenticator authenticator)
    {
        _server = server;
        _connection = connection;
        _options = options;
        _engines = engines;
        _authenticator = authenticator;
        _lifetimeSource = CancellationTokenSource.CreateLinkedTokenSource(connection.ConnectionClosed);
    }

    /// <inheritdoc />
    public Guid Id { get; } = Guid.NewGuid();

    /// <inheritdoc />
    public ProtocolVersion ProtocolVersion { get; private set; }

    /// <inheritdoc />
    public string? Principal { get; private set; }

    /// <inheritdoc />
    public IDatabaseSession? DatabaseSession => _databaseSession;

    /// <summary>
    /// Gets the task that completes when the session pump has fully wound down.
    /// Never faults — the pump owns its errors.
    /// </summary>
    internal Task Completion => _completion;

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
    public async ValueTask DisposeAsync()
    {
        Abort();
        await _completion.ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationToken softStop, CancellationToken hardAbort)
    {
        // Yield so Start returns immediately and registration completes before frames flow.
        await Task.Yield();

        CancellationTokenRegistration abortRegistration = hardAbort.Register(static state => ((DatabaseServerSession)state!).Abort(), this);

        Stream stream = _connection.AsStream();
        _reader = ProtocolFraming.CreateReader(stream, leaveOpen: true);
        _writer = ProtocolFraming.CreateWriter(stream, leaveOpen: true);

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
            await TryWriteErrorAsync(ProtocolErrorCode.ProtocolViolation, exception.Message).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Aborted, connection closed, or stop signaled mid-frame.
        }
        catch (ConnectionAbortedException)
        {
        }
        catch (IOException)
        {
            // The transport failed under the pump; nothing to report to the peer.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
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
            return false;
        }

        if (frame is null)
        {
            return false; // The peer closed before starting up.
        }

        if (frame.Value.Type != ProtocolMessageType.Startup)
        {
            await TryWriteErrorAsync(ProtocolErrorCode.ProtocolViolation, $"Expected a startup frame but received {frame.Value.Type}.").ConfigureAwait(false);
            return false;
        }

        ProtocolStartupMessage startup = ProtocolStartupMessage.Decode(frame.Value.Payload.Span);

        if (startup.Version.Major != ProtocolVersion.Current.Major)
        {
            await TryWriteErrorAsync(ProtocolErrorCode.UnsupportedVersion, $"Protocol major version {startup.Version.Major} is not supported; the server speaks {ProtocolVersion.Current}.").ConfigureAwait(false);
            return false;
        }

        ProtocolVersion = ProtocolVersion.Current;

        IDatabase? database = await ResolveDatabaseAsync(startup.Database, handshakeSource.Token).ConfigureAwait(false);

        if (database is null)
        {
            await TryWriteErrorAsync(ProtocolErrorCode.DatabaseNotFound, $"No registered engine has a database named '{startup.Database}'.").ConfigureAwait(false);
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
            return false;
        }

        if (frame is null)
        {
            return false;
        }

        if (frame.Value.Type != ProtocolMessageType.AuthenticateResponse)
        {
            await TryWriteErrorAsync(ProtocolErrorCode.ProtocolViolation, $"Expected an authenticate response but received {frame.Value.Type}.").ConfigureAwait(false);
            return false;
        }

        bool authenticated = await _authenticator.AuthenticateAsync(startup.Database, startup.Principal, frame.Value.Payload, handshakeSource.Token).ConfigureAwait(false);

        if (!authenticated)
        {
            await TryWriteErrorAsync(ProtocolErrorCode.AuthenticationFailed, $"Authentication failed for principal '{startup.Principal}'.").ConfigureAwait(false);
            return false;
        }

        _databaseSession = await database.CreateSessionAsync(handshakeSource.Token).ConfigureAwait(false);
        Principal = startup.Principal;

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
                    await TryWriteErrorAsync(ProtocolErrorCode.Unavailable, "The server is shutting down.").ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (!_lifetimeSource.IsCancellationRequested)
                {
                    // Idle timeout eviction.
                    await TryWriteErrorAsync(ProtocolErrorCode.Unavailable, "The session was closed after exceeding the idle timeout.").ConfigureAwait(false);
                    return;
                }
            }

            if (frame is null)
            {
                return; // The peer closed cleanly between frames.
            }

            switch (frame.Value.Type)
            {
                case ProtocolMessageType.Execute:
                    // Executions run on the session lifetime token, not the soft-stop
                    // token: a drain lets in-flight statements finish.
                    await ExecuteAsync(frame.Value, rowWriter, _lifetimeSource.Token).ConfigureAwait(false);
                    break;

                case ProtocolMessageType.Ping:
                    await WriteFrameAsync(ProtocolMessageType.Pong, ReadOnlyMemory<byte>.Empty, _lifetimeSource.Token).ConfigureAwait(false);
                    break;

                case ProtocolMessageType.Terminate:
                    return;

                default:
                    await TryWriteErrorAsync(ProtocolErrorCode.ProtocolViolation, $"Unexpected {frame.Value.Type} frame in the ready state.").ConfigureAwait(false);
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

                await WriteFrameAsync(ProtocolMessageType.ResultHeader, new ProtocolResultHeaderMessage(columns).Encode(), cancellationToken).ConfigureAwait(false);

                await foreach (QueryRow row in resultSet.GetRowsAsync(cancellationToken).ConfigureAwait(false))
                {
                    rowWriter.Reset();

                    for (int ordinal = 0; ordinal < row.FieldCount; ordinal++)
                    {
                        DatabaseValueCodec.Append(rowWriter, row.GetValue(ordinal));
                    }

                    await WriteFrameAsync(ProtocolMessageType.ResultRow, rowWriter.ToArray(), cancellationToken).ConfigureAwait(false);
                }

                await WriteFrameAsync(ProtocolMessageType.ResultComplete, new ProtocolResultCompleteMessage(-1).Encode(), cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        if (result.Status != QueryResultStatus.Success)
        {
            string detail = result.Diagnostics is { Count: > 0 } diagnostics && diagnostics[0].Message is { } diagnosticMessage
                ? diagnosticMessage
                : $"The statement completed with status {result.Status}.";

            await WriteErrorAsync(ProtocolErrorCode.ExecutionFailure, detail, cancellationToken).ConfigureAwait(false);
            return;
        }

        await WriteFrameAsync(ProtocolMessageType.ResultComplete, new ProtocolResultCompleteMessage(result.AffectedCount).Encode(), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<IDatabase?> ResolveDatabaseAsync(string name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        foreach (IDatabaseEngine engine in _engines)
        {
            if (engine.TryGetDatabase(name, out IDatabase database))
            {
                return database;
            }
        }

        foreach (IDatabaseEngine engine in _engines)
        {
            try
            {
                return await engine.OpenDatabaseAsync(name, cancellationToken).ConfigureAwait(false);
            }
            catch (DatabaseException)
            {
                // Not this engine's database; try the next one.
            }
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

    private async ValueTask CleanupAsync()
    {
        if (_databaseSession is not null)
        {
            try
            {
                await _databaseSession.DisposeAsync().ConfigureAwait(false);
            }
            catch (DatabaseException)
            {
                // Session teardown must not mask the pump outcome.
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
