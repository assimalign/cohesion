using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Security;
using Assimalign.Cohesion.Database.Types;

using Assimalign.Cohesion.Database.Sql.Protocol;

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
[global::System.Diagnostics.DebuggerDisplay("SqlDatabaseServerSession")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlDatabaseServerSession.DebugView))]
internal sealed partial class SqlDatabaseServerSession : DatabaseServerSession
{
    private readonly SqlDatabaseServer _server;
    private readonly IDuplexConnection _connection;
    private readonly SqlDatabaseServerOptions _options;
    private readonly SqlDatabaseEngine _engine;
    private readonly DatabaseAuthenticator _authenticator;
    private readonly CancellationTokenSource _lifetimeSource;
    private readonly long _acceptedTimestamp;

    private ProtocolChannel? _channel;
    private ProtocolFrameReader? _reader;
    private ProtocolFrameWriter? _writer;
    private SqlDatabaseSession? _databaseSession;
    private bool _terminalResponseWritten;
    private CancellationTokenSource? _terminalDrainSource;
    private CancellationTokenSource? _terminalDeadlineSource;
    private CancellationTokenRegistration _terminalDrainRegistration;
    private Task _completion = Task.CompletedTask;
    private string _closeReason = SqlDatabaseEventSource.CloseReason.Unknown;

    // Set by Abort before it cancels the lifetime, so the pump can tell the server's own teardown
    // from a transport that closed under it.
    private int _abortRequested;

    internal SqlDatabaseServerSession(
        SqlDatabaseServer server,
        IDuplexConnection connection,
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

    // Owning-assembly test seam for result diagnostics and late result failures.
    internal Func<DatabaseResult>? ExecuteResultOverride { get; set; }

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
    internal void Abort() => Abort(terminalDeadline: false);

    private void Abort(bool terminalDeadline)
    {
        // Claim abort before cancelling the lifetime: the terminal deadline links back here.
        // No lock is held across cancellation callbacks or transport I/O.
        if (Interlocked.Exchange(ref _abortRequested, 1) != 0) { return; }
        if (terminalDeadline)
        {
            SqlDatabaseEventSource.Log.TerminalDrainTimedOut(this, _options.ShutdownDrainTimeout);
        }
        try
        {
            _lifetimeSource.Cancel();
            _connection.Abort(new ConnectionAbortedException(ErrorMessages.SqlDatabaseServerSessionAbortFailure));
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

        try
        {
            Stream stream = _connection.AsStream();
            _channel = new ProtocolChannel(stream, SqlProtocol.Family, leaveOpen: true);
            _reader = _channel.Reader;
            _writer = _channel.Writer;

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
        catch (OperationCanceledException) when (ClosedUnderPump(softStop, hardAbort))
        {
            // The transport closed under a blocked read or write: a peer that reset or hung up. The
            // session's lifetime follows the connection's ConnectionClosed, which a duplex transport
            // signals as soon as it sees the close (#1459), so the in-flight operation ends in a
            // cancellation the server did not request. That is a transport outcome (plan D9).
            _closeReason = SqlDatabaseEventSource.CloseReason.TransportFailed;
        }
        catch (OperationCanceledException) when (softStop.IsCancellationRequested && !_lifetimeSource.IsCancellationRequested && _databaseSession is not null)
        {
            // Ready can reach the peer before its explicit flush resumes. If soft stop
            // cancels that final handshake flush, the authenticated peer still needs its
            // terminal notice, just as one already inside the ready loop does.
            _closeReason = SqlDatabaseEventSource.CloseReason.Shutdown;
            await TryWriteErrorAsync(ProtocolErrorCode.Unavailable, ErrorMessages.SqlDatabaseServerSessionAbortFailure).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The server stopped or aborted the session mid-frame.
            _closeReason = SqlDatabaseEventSource.CloseReason.Cancelled;
        }
        catch (Exception exception) when (TransportCloseReason(exception) is { } reason)
        {
            // The transport failed under the pump: a peer that hung up or reset the connection, or
            // the shutdown's abort. An expected outcome (plan D9), which SessionClosed's reason
            // carries; nothing is reported to the peer.
            _closeReason = reason;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The peer gets an internal error frame only; the event carries the failure.
            _closeReason = SqlDatabaseEventSource.CloseReason.Faulted;
            SqlDatabaseEventSource.Log.SessionFaulted(this, exception);
            await TryWriteErrorAsync(ProtocolErrorCode.Internal, ErrorMessages.ServerSessionRunAsyncValueTask).ConfigureAwait(false);
        }
        finally
        {
            await DrainTerminalResponseAsync().ConfigureAwait(false);
            // A write can resume inline before the deadline's abort callback runs.
            if (_terminalDeadlineSource?.IsCancellationRequested == true) { OnTerminalDrainDeadline(); }
            // Detach from the abort signal before the lifetime source is disposed
            // so a late hard abort cannot race a disposed token source.
            await abortRegistration.DisposeAsync().ConfigureAwait(false);
            await _terminalDrainRegistration.DisposeAsync().ConfigureAwait(false);
            _terminalDrainSource?.Dispose();
            _terminalDeadlineSource?.Dispose();
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

        try
        {
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
                await RefuseHandshakeAsync(ProtocolErrorCode.ProtocolViolation, string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.StartupFrameExpected, frame.Value.Type), string.Empty, string.Empty).ConfigureAwait(false);
                return false;
            }

            ProtocolStartupMessage startup = ProtocolStartupMessage.Decode(frame.Value.Payload.Span);

            if (!ProtocolVersion.TryNegotiate(startup.Version, out var negotiatedVersion))
            {
                await RefuseHandshakeAsync(ProtocolErrorCode.UnsupportedVersion, string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ProtocolVersionUnsupported, startup.Version.Major, ProtocolVersion.Current), startup.Database, startup.Principal).ConfigureAwait(false);
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
                await RefuseHandshakeAsync(ProtocolErrorCode.DatabaseNotFound, string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.HandshakeDatabaseMissing, startup.Database), startup.Database, startup.Principal).ConfigureAwait(false);
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
                await RefuseHandshakeAsync(ProtocolErrorCode.ProtocolViolation, string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.AuthenticationResponseExpected, frame.Value.Type), startup.Database, startup.Principal).ConfigureAwait(false);
                return false;
            }

            bool authenticated = await _authenticator.AuthenticateAsync(startup.Database, startup.Principal, frame.Value.Payload, handshakeSource.Token).ConfigureAwait(false);

            if (!authenticated)
            {
                await RefuseHandshakeAsync(ProtocolErrorCode.AuthenticationFailed, string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.PrincipalAuthenticationFailed, startup.Principal), startup.Database, startup.Principal).ConfigureAwait(false);
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
                await RefuseHandshakeAsync(ProtocolErrorCode.Unavailable, $"{exception.Code}: {exception.Message}", startup.Database, startup.Principal).ConfigureAwait(false);
                return false;
            }

            SetAuthenticatedPrincipal(startup.Principal);

            await WriteFrameAsync(ProtocolMessageType.Ready, _channel!.Negotiate(negotiatedVersion, startup.Capabilities).Encode(), handshakeSource.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (handshakeSource.IsCancellationRequested && !_lifetimeSource.IsCancellationRequested && !softStop.IsCancellationRequested)
        {
            // The authentication timeout lapsed outside the two reads: while the database was
            // resolved or opened, a handshake frame was written, the authenticator ran, or the
            // session was created. The connection is dropped without an error frame, as at a
            // read, and the pump ends the session as it did when the cancellation reached it.
            ReportHandshakeTimeout();
            return false;
        }
    }

    private async Task ReadyLoopAsync(CancellationToken softStop)
    {

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
                    await TryWriteErrorAsync(ProtocolErrorCode.Unavailable, ErrorMessages.SqlDatabaseServerSessionAbortFailure).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (!_lifetimeSource.IsCancellationRequested)
                {
                    // Idle timeout eviction.
                    _closeReason = SqlDatabaseEventSource.CloseReason.IdleTimeout;
                    await TryWriteErrorAsync(ProtocolErrorCode.Unavailable, ErrorMessages.ServerSessionReadyLoopAsyncValueTask).ConfigureAwait(false);
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
                case (ProtocolMessageType)SqlProtocolMessageType.CopyTo:
                    await CopyToAsync(frame.Value, _lifetimeSource.Token).ConfigureAwait(false);
                    await WriteTransactionStatusAsync().ConfigureAwait(false);
                    break;
                case (ProtocolMessageType)SqlProtocolMessageType.Execute:
                    // Executions run on the session lifetime token, not the soft-stop
                    // token: a drain lets in-flight statements finish.
                    await ExecuteAsync(frame.Value, _lifetimeSource.Token).ConfigureAwait(false);
                    if (_channel!.Capabilities.Contains(ProtocolCapability.TransactionStatus.Name))
                    {
                        var transaction = _databaseSession!.CurrentTransaction;
                        var status = transaction is null ? ProtocolTransactionStatus.Idle
                            : transaction.State == Assimalign.Cohesion.Database.Transactions.TransactionState.Faulted
                                ? ProtocolTransactionStatus.Failed : ProtocolTransactionStatus.Active;
                        await WriteFrameAsync(ProtocolMessageType.Ready,
                            new ProtocolTransactionStatusMessage(status, _databaseSession!.HasRetainedState).Encode(), _lifetimeSource.Token).ConfigureAwait(false);
                    }
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

    private async ValueTask WriteTransactionStatusAsync()
    {
        if (!_channel!.Capabilities.Contains(ProtocolCapability.TransactionStatus.Name)) { return; }
        var transaction = _databaseSession!.CurrentTransaction;
        var status = transaction is null ? ProtocolTransactionStatus.Idle
            : transaction.State == Assimalign.Cohesion.Database.Transactions.TransactionState.Faulted
                ? ProtocolTransactionStatus.Failed : ProtocolTransactionStatus.Active;
        await WriteFrameAsync(ProtocolMessageType.Ready,
            new ProtocolTransactionStatusMessage(status, _databaseSession!.HasRetainedState).Encode(), _lifetimeSource.Token).ConfigureAwait(false);
    }

    private async Task ExecuteAsync(ProtocolFrame frame, CancellationToken cancellationToken)
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
                    throw new ProtocolException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ServerSessionExecuteAsyncProtocol, name), exception);
                }
            }
        }

        DatabaseResult result;
        var preparation = new SqlResultPreparation(ExecuteResultOverride);
        _databaseSession!.ResultPreparation = preparation;

        try
        {
            result = await _databaseSession.ExecuteAsync(message.Statement, parameters, cancellationToken).ConfigureAwait(false);
        }
        catch (DatabaseParseException exception)
        {
            // Statement-level failures keep the session in the ready state.
            await WriteDiagnosticsAsync(PrepareDiagnostics(exception.Diagnostics), cancellationToken).ConfigureAwait(false);
            await WriteErrorAsync(ProtocolErrorCode.ParseFailure, exception.Message, cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (DatabaseOfflineException exception)
        {
            // The database is offline (#1243): every statement is refused, with the coded
            // reason, until it is reopened; the session stays ready to report it.
            await WriteDiagnosticsAsync(PrepareDiagnostics(exception.Diagnostics), cancellationToken).ConfigureAwait(false);
            await WriteErrorAsync(ProtocolErrorCode.Unavailable, string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ServerSessionExecuteAsyncValueTask, exception.Code, exception.Message), cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (DatabaseException exception)
        {
            var diagnostic = exception.Diagnostics.FirstOrDefault(item => item.Severity == DiagnosticSeverity.Error);
            string errorText = diagnostic?.Code is { } code && !exception.Message.StartsWith(code + ":", StringComparison.Ordinal) ? string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ServerSessionExecuteAsyncValueTask, code, exception.Message) : exception.Message;
            await WriteDiagnosticsAsync(PrepareDiagnostics(exception.Diagnostics), cancellationToken).ConfigureAwait(false);
            await WriteErrorAsync(ProtocolErrorCode.ExecutionFailure, errorText, cancellationToken).ConfigureAwait(false);
            return;
        }

        finally { _databaseSession.ResultPreparation = null; }

        // Writes finished all fallible result work before committing. Reads encode during delivery.
        await WriteResultAsync(result, preparation, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteResultAsync(DatabaseResult result, SqlResultPreparation preparation, CancellationToken cancellationToken)
    {
        if (!preparation.IsPrepared)
        {
            // No write used this preparation. A late result failure can safely end this exchange;
            // the materialized read already released statement admission and wrote nothing.
            try
            {
                result = await preparation.OverrideAsync(result).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is DatabaseException or DatabaseTypeException)
            {
                // An execution error raised by the test seam retains its original diagnostics.
                await WriteResultFailureAsync(exception as DatabaseException ?? SqlResultPreparation.TranslateFailure(exception), cancellationToken).ConfigureAwait(false);
                return;
            }
            if (result is DatabaseResultSet readOnly)
            {
                await WriteReadOnlyResultAsync(readOnly, preparation, PrepareDiagnostics(result.Diagnostics), cancellationToken).ConfigureAwait(false);
                return;
            }
        }
        byte[]? diagnosticPayload = PrepareDiagnostics(result.Diagnostics);
        if (result is DatabaseResultSet)
        {
            await WriteFrameAsync((ProtocolMessageType)SqlProtocolMessageType.ResultHeader, preparation.Header!, cancellationToken).ConfigureAwait(false);
            foreach (byte[] row in preparation.Rows)
            {
                await WriteFrameAsync((ProtocolMessageType)SqlProtocolMessageType.ResultRow, row, cancellationToken).ConfigureAwait(false);
            }
            await WriteDiagnosticsAsync(diagnosticPayload, cancellationToken).ConfigureAwait(false);
            await WriteFrameAsync((ProtocolMessageType)SqlProtocolMessageType.ResultComplete, preparation.Completion!, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (result.Status != DatabaseResultStatus.Success)
        {
            string detail = result.Diagnostics is { Count: > 0 } diagnostics && diagnostics[0].Message is { } diagnosticMessage
                ? string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ServerSessionExecuteAsyncValueTask, diagnostics[0].Code, diagnosticMessage)
                : string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SessionWriteResultAsyncValueTask, result.Status);

            await WriteDiagnosticsAsync(diagnosticPayload, cancellationToken).ConfigureAwait(false);
            await WriteErrorAsync(ProtocolErrorCode.ExecutionFailure, detail, cancellationToken).ConfigureAwait(false);
            return;
        }

        await WriteDiagnosticsAsync(diagnosticPayload, cancellationToken).ConfigureAwait(false);
        await WriteFrameAsync((ProtocolMessageType)SqlProtocolMessageType.ResultComplete,
            preparation.Completion ?? new ProtocolResultCompleteMessage(result.AffectedCount).Encode(), cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteReadOnlyResultAsync(DatabaseResultSet result, SqlResultPreparation encoder,
        byte[]? diagnosticPayload, CancellationToken cancellationToken)
    {
        bool transportFailed = false;
        byte[] completion;
        try
        {
            await using (result.ConfigureAwait(false))
            {
                await WriteAsync((ProtocolMessageType)SqlProtocolMessageType.ResultHeader, encoder.EncodeHeader(result, cancellationToken)).ConfigureAwait(false);
                await foreach (DatabaseRow row in result.GetRowsAsync(cancellationToken).ConfigureAwait(false))
                {
                    await WriteAsync((ProtocolMessageType)SqlProtocolMessageType.ResultRow, encoder.EncodeRow(row, cancellationToken)).ConfigureAwait(false);
                }
                completion = new ProtocolResultCompleteMessage(result.AffectedCount).Encode();
            }
        }
        catch (Exception exception) when (!transportFailed && exception is DatabaseException or DatabaseTypeException)
        {
            await WriteResultFailureAsync(SqlResultPreparation.TranslateFailure(exception), cancellationToken).ConfigureAwait(false);
            return;
        }
        await WriteDiagnosticsAsync(diagnosticPayload, cancellationToken).ConfigureAwait(false);
        await WriteFrameAsync((ProtocolMessageType)SqlProtocolMessageType.ResultComplete, completion, cancellationToken).ConfigureAwait(false);

        async Task WriteAsync(ProtocolMessageType type, byte[] payload)
        {
            try { await WriteFrameAsync(type, payload, cancellationToken).ConfigureAwait(false); }
            // A delivery failure stays terminal even if disposal also fails while unwinding it.
            catch { transportFailed = true; throw; }
        }
    }

    private async Task WriteResultFailureAsync(DatabaseException exception, CancellationToken cancellationToken)
    {
        var diagnostic = exception.Diagnostics.FirstOrDefault(item => item.Severity == DiagnosticSeverity.Error);
        string errorText = diagnostic?.Code is { } code && !exception.Message.StartsWith(code + ":", StringComparison.Ordinal)
            ? string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ServerSessionExecuteAsyncValueTask, code, exception.Message) : exception.Message;
        await WriteDiagnosticsAsync(PrepareDiagnostics(exception.Diagnostics), cancellationToken).ConfigureAwait(false);
        await WriteErrorAsync(ProtocolErrorCode.ExecutionFailure, errorText, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the startup-requested database on the server's one engine:
    /// already-open databases first, then an open attempt.
    /// </summary>
    /// <returns>The database, or <see langword="null"/> when the engine has none by that name.</returns>
    /// <exception cref="SqlDataStorageFormatException">The database is on a data-storage format the engine refuses.</exception>
    private async ValueTask<SqlDatabase?> ResolveDatabaseAsync(string name, CancellationToken cancellationToken)
    {
        // A name that breaks the area's name rule names no database the engine can hold, so it is
        // not found, instead of the open's ArgumentException faulting the session (#1392).
        if (string.IsNullOrWhiteSpace(name) || DatabaseFileNames.GetRefusal(name) is not null)
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

    // Prepare the complete bounded payload before any result or Error reaches the wire.
    private byte[]? PrepareDiagnostics(IReadOnlyList<Diagnostic> diagnostics)
    {
        if (diagnostics.Count == 0 ||
            !_channel!.Capabilities.Contains(ProtocolCapability.Diagnostics.Name)) { return null; }
        var entries = new List<ProtocolDiagnostic>();
        int size = sizeof(int);
        int total = 0;
        bool overflow = false;
        foreach (Diagnostic item in diagnostics)
        {
            if (item.Severity is not (DiagnosticSeverity.Warning or DiagnosticSeverity.Error)) { continue; }
            total++;
            long entrySize = DiagnosticSize(item.Code, item.Message);
            if (overflow || entries.Count == ProtocolDiagnosticsMessage.MaxEntryCount ||
                entrySize > ProtocolDiagnosticsMessage.MaxPayloadLength - size)
            {
                overflow = true;
                continue;
            }
            entries.Add(new ProtocolDiagnostic(
                item.Severity == DiagnosticSeverity.Warning ? ProtocolDiagnosticSeverity.Warning : ProtocolDiagnosticSeverity.Error,
                item.Code, item.Message, item.Start, item.End, item.Line));
            size += (int)entrySize;
        }
        if (overflow)
        {
            while (true)
            {
                string notice = FormattableString.Invariant($"Diagnostic overflow: retained the first {entries.Count} of {total} Warning/Error entries in source order; omitted the remaining {total - entries.Count}. Limits: 4096 entries and 1048576 encoded bytes.");
                if (entries.Count < ProtocolDiagnosticsMessage.MaxEntryCount &&
                    DiagnosticSize("CK07001", notice) <= ProtocolDiagnosticsMessage.MaxPayloadLength - size)
                {
                    entries.Add(new ProtocolDiagnostic(ProtocolDiagnosticSeverity.Warning, "CK07001", notice, null, null, 1));
                    break;
                }
                ProtocolDiagnostic last = entries[^1];
                size -= (int)DiagnosticSize(last.Code, last.Message);
                entries.RemoveAt(entries.Count - 1);
            }
        }
        return entries.Count == 0 ? null : new ProtocolDiagnosticsMessage(entries).Encode();
    }

    private static long DiagnosticSize(string? code, string? message)
        => 21L + System.Text.Encoding.UTF8.GetByteCount(code ?? string.Empty) + System.Text.Encoding.UTF8.GetByteCount(message ?? string.Empty);

    private ValueTask WriteDiagnosticsAsync(byte[]? payload, CancellationToken cancellationToken)
        => payload is null ? ValueTask.CompletedTask : WriteFrameAsync(ProtocolMessageType.Diagnostics, payload, cancellationToken);

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
            // Start one budget before any terminal I/O. Aborting on cancellation also releases
            // transports whose write/flush ignores tokens or whose completion has no token.
            if (_terminalDrainSource is null)
            {
                _terminalDeadlineSource = new CancellationTokenSource();
                _terminalDrainSource = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeSource.Token, _terminalDeadlineSource.Token);
                _terminalDrainRegistration = _terminalDeadlineSource.Token.Register(static state => ((SqlDatabaseServerSession)state!).OnTerminalDrainDeadline(), this);
                _terminalDeadlineSource.CancelAfter(_options.ShutdownDrainTimeout);
            }
            await WriteErrorAsync(code, message, _terminalDrainSource.Token).ConfigureAwait(false);
            _terminalResponseWritten = true;
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
        string violation = string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.RejectUnexpectedFrameAsyncValueTask, type);
        _closeReason = SqlDatabaseEventSource.CloseReason.ProtocolViolation;
        SqlDatabaseEventSource.Log.SessionProtocolViolation(this, violation);
        return TryWriteErrorAsync(ProtocolErrorCode.ProtocolViolation, violation);
    }

    /// <summary>
    /// Classifies a failure that reached the pump as the transport's, and names the close reason it
    /// gives the session; null for anything else, which stays a fault. The four model servers carry
    /// identical copies (plan D2). A reset reaches the pump as the TCP driver's raw
    /// <see cref="SocketException"/> or a <see cref="ConnectionException"/>, neither of which is an
    /// <see cref="IOException"/>; a stream that wraps one in an <see cref="IOException"/> is still
    /// the transport's. A bare <see cref="IOException"/> is not: it can be the storage device's.
    /// </summary>
    /// <param name="exception">What reached the pump.</param>
    /// <returns><c>ConnectionAborted</c>, <c>TransportFailed</c>, or null.</returns>
    // True when the transport's close, not the server's stop or abort, cancelled the pump.
    private bool ClosedUnderPump(CancellationToken softStop, CancellationToken hardAbort)
        => !softStop.IsCancellationRequested
            && !hardAbort.IsCancellationRequested
            && Volatile.Read(ref _abortRequested) == 0
            && _connection.ConnectionClosed.IsCancellationRequested;

    private static string? TransportCloseReason(Exception exception) => exception switch
    {
        ConnectionAbortedException => SqlDatabaseEventSource.CloseReason.ConnectionAborted,
        ConnectionException => SqlDatabaseEventSource.CloseReason.TransportFailed,
        SocketException => SqlDatabaseEventSource.CloseReason.TransportFailed,
        IOException { InnerException: SocketException or ConnectionException } => SqlDatabaseEventSource.CloseReason.TransportFailed,
        _ => null,
    };

    // Claim the same abort transition as hard stop. A deadline that loses that race
    // cannot report a second cause for an already aborted transport.
    private void OnTerminalDrainDeadline() => Abort(terminalDeadline: true);

    private async ValueTask DrainTerminalResponseAsync()
    {
        if (!_terminalResponseWritten) { return; }
        try
        {
            // Flush queues bytes in a transport pipe. Completing output lets its send pump
            // drain them before disposal can abort it. A peer that keeps its half open is
            // bounded by the deadline already running since before the terminal write. Keep
            // the abort registrations while joining completion: cancellation must abort the
            // transport, not abandon a pending operation that still owns its output.
            await _connection.Output.CompleteAsync().ConfigureAwait(false);
            // Output is complete. Bound the peer-close wait separately so an unread
            // pooled notice cannot hold StopAsync or a session slot for the outer budget.
            using var linger = CancellationTokenSource.CreateLinkedTokenSource(_terminalDrainSource!.Token);
            linger.CancelAfter(_options.TerminalDrainLinger);
            while (true)
            {
                var result = await _connection.Input.ReadAsync(linger.Token).ConfigureAwait(false);
                _connection.Input.AdvanceTo(result.Buffer.End);
                if (result.IsCompleted || result.IsCanceled) { break; }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { /* Output completion can close a TLS reader. */ }
        catch (Exception exception) when (TransportCloseReason(exception) is not null)
        {
            // A terminal peer can reset or abort after consuming its error frame.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            SqlDatabaseEventSource.Log.SessionCleanupFailed(this, exception);
        }
    }

    private async ValueTask CleanupAsync()
    {
        try
        {
            // Teardown owns failures just like the pump. Attempt every release,
            // including the connection, when an earlier disposal fails.
            IAsyncDisposable?[] resources = [_databaseSession, _channel, _connection];
            foreach (var resource in resources)
            {
                if (resource is null) { continue; }
                try
                {
                    await resource.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // This session is terminal; remaining resources still need cleanup.
                    SqlDatabaseEventSource.Log.SessionCleanupFailed(this, exception);
                }
            }
        }
        finally
        {
            _lifetimeSource.Dispose();
            _server.OnSessionCompleted(this);
        }
    }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Internal.SqlDatabaseServerSession _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlDatabaseServerSession value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Sql.SqlDatabaseSession? DatabaseSession => _target.DatabaseSession;
        public global::System.Guid Id => _target.Id;
        public string? Principal => _target.Principal;
    }
}
