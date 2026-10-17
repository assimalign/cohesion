using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Database.Documents.Catalog;
using Assimalign.Cohesion.Database.Documents.Language;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Security;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents.Internal;

/// <summary>
/// One server-side session pump: drives the protocol state machine
/// (connected → startup → authenticating → ready ⇄ executing → terminated)
/// over a single connection and delegates statement execution to the bound
/// engine session's internal ExecuteStatementAsync entry.
/// </summary>
/// <remarks>
/// An internal sealed leaf of the root <see cref="DatabaseServerSession"/> (concrete-types plan,
/// row 11): the base owns the identity, and the negotiated version and authenticated principal,
/// which the handshake records once each; the engine session is re-exposed typed by a covariant
/// override.
/// </remarks>
internal sealed class DocumentDatabaseServerSession : DatabaseServerSession
{
    private static readonly string _shutdownMessage = ErrorMessages.ServerShuttingDown;

    private readonly DocumentDatabaseServer _server;
    private readonly IDuplexConnection _connection;
    private readonly DocumentDatabaseServerOptions _options;
    private readonly DocumentDatabaseEngine _engine;
    private readonly DatabaseAuthenticator _authenticator;
    private readonly CancellationTokenSource _lifetimeSource;
    private readonly long _acceptedTimestamp;

    private ProtocolChannel? _channel;
    private ProtocolFrameReader? _reader;
    private ProtocolFrameWriter? _writer;
    private DocumentDatabaseSession? _databaseSession;
    private bool _terminalResponseWritten;
    private CancellationTokenSource? _terminalDrainSource;
    private CancellationTokenRegistration _terminalDrainRegistration;
    private Task _completion = Task.CompletedTask;
    private string _closeReason = DocumentDatabaseEventSource.CloseReason.Unknown;

    // Set by Abort before it cancels the lifetime, so the pump can tell the server's own teardown
    // from a transport that closed under it.
    private int _abortRequested;

    internal DocumentDatabaseServerSession(
        DocumentDatabaseServer server,
        IDuplexConnection connection,
        DocumentDatabaseServerOptions options,
        DocumentDatabaseEngine engine,
        DatabaseAuthenticator authenticator)
    {
        _server = server;
        _connection = connection;
        _options = options;
        _engine = engine;
        _authenticator = authenticator;
        _lifetimeSource = CancellationTokenSource.CreateLinkedTokenSource(connection.ConnectionClosed);
        _acceptedTimestamp = DocumentDatabaseEventSource.Log.SessionTimestamp();
    }

    /// <inheritdoc />
    public override DocumentDatabaseSession? DatabaseSession => _databaseSession;

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
        // The drain deadline, connection close and hard abort can converge here. Claim abort
        // before cancelling the lifetime, whose linked terminal deadline calls back here too.
        if (Interlocked.Exchange(ref _abortRequested, 1) != 0) { return; }
        try
        {
            _lifetimeSource.Cancel();
            _connection.Abort(new ConnectionAbortedException(_shutdownMessage));
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

        CancellationTokenRegistration abortRegistration = hardAbort.Register(static state => ((DocumentDatabaseServerSession)state!).Abort(), this);

        try
        {
            Stream stream = _connection.AsStream();
            _channel = new ProtocolChannel(stream, DocumentProtocol.Family, leaveOpen: true);
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
            _closeReason = DocumentDatabaseEventSource.CloseReason.ProtocolViolation;
            DocumentDatabaseEventSource.Log.SessionProtocolViolation(this, exception.Message);
            await TryWriteErrorAsync(ProtocolErrorCode.ProtocolViolation, exception.Message).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ClosedUnderPump(softStop, hardAbort))
        {
            // The transport closed under a blocked read or write: a peer that reset or hung up. The
            // session's lifetime follows the connection's ConnectionClosed, which a duplex transport
            // signals as soon as it sees the close (#1459), so the in-flight operation ends in a
            // cancellation the server did not request. That is a transport outcome (plan D9).
            _closeReason = DocumentDatabaseEventSource.CloseReason.TransportFailed;
        }
        catch (OperationCanceledException)
        {
            // The server stopped or aborted the session mid-frame.
            _closeReason = DocumentDatabaseEventSource.CloseReason.Cancelled;
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
            _closeReason = DocumentDatabaseEventSource.CloseReason.Faulted;
            DocumentDatabaseEventSource.Log.SessionFaulted(this, exception);
            await TryWriteErrorAsync(ProtocolErrorCode.Internal, ErrorMessages.ServerSessionFaulted).ConfigureAwait(false);
        }
        finally
        {
            await DrainTerminalResponseAsync(softStop).ConfigureAwait(false);
            // Detach from the abort signal before the lifetime source is disposed
            // so a late hard abort cannot race a disposed token source.
            await abortRegistration.DisposeAsync().ConfigureAwait(false);
            await _terminalDrainRegistration.DisposeAsync().ConfigureAwait(false);
            _terminalDrainSource?.Dispose();
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
                _closeReason = DocumentDatabaseEventSource.CloseReason.PeerClosed;
                return false; // The peer closed before starting up.
            }

            if (frame.Value.Type != ProtocolMessageType.Startup)
            {
                await RefuseHandshakeAsync(ProtocolErrorCode.ProtocolViolation, string.Format(ErrorMessages.StartupExpected, frame.Value.Type), string.Empty, string.Empty).ConfigureAwait(false);
                return false;
            }

            ProtocolStartupMessage startup = ProtocolStartupMessage.Decode(frame.Value.Payload.Span);

            if (!ProtocolVersion.TryNegotiate(startup.Version, out var negotiated))
            {
                await RefuseHandshakeAsync(ProtocolErrorCode.UnsupportedVersion, string.Format(ErrorMessages.ProtocolVersionUnsupported, startup.Version.Major, ProtocolVersion.Current), startup.Database, startup.Principal).ConfigureAwait(false);
                return false;
            }

            SetNegotiatedVersion(negotiated);

            DocumentDatabase? database = await ResolveDatabaseAsync(startup.Database, handshakeSource.Token).ConfigureAwait(false);

            if (database is null)
            {
                await RefuseHandshakeAsync(ProtocolErrorCode.DatabaseNotFound, string.Format(ErrorMessages.DatabaseNotFound, startup.Database), startup.Database, startup.Principal).ConfigureAwait(false);
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
                _closeReason = DocumentDatabaseEventSource.CloseReason.PeerClosed;
                return false;
            }

            if (frame.Value.Type != ProtocolMessageType.AuthenticateResponse)
            {
                await RefuseHandshakeAsync(ProtocolErrorCode.ProtocolViolation, string.Format(ErrorMessages.AuthenticationResponseExpected, frame.Value.Type), startup.Database, startup.Principal).ConfigureAwait(false);
                return false;
            }

            bool authenticated = await _authenticator.AuthenticateAsync(startup.Database, startup.Principal, frame.Value.Payload, handshakeSource.Token).ConfigureAwait(false);

            if (!authenticated)
            {
                await RefuseHandshakeAsync(ProtocolErrorCode.AuthenticationFailed, string.Format(ErrorMessages.AuthenticationFailed, startup.Principal), startup.Database, startup.Principal).ConfigureAwait(false);
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
                    _closeReason = DocumentDatabaseEventSource.CloseReason.Shutdown;
                    await TryWriteErrorAsync(ProtocolErrorCode.Unavailable, _shutdownMessage).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (!_lifetimeSource.IsCancellationRequested)
                {
                    // Idle timeout eviction.
                    _closeReason = DocumentDatabaseEventSource.CloseReason.IdleTimeout;
                    await TryWriteErrorAsync(ProtocolErrorCode.Unavailable, ErrorMessages.IdleTimeoutExceeded).ConfigureAwait(false);
                    return;
                }
            }

            if (frame is null)
            {
                _closeReason = DocumentDatabaseEventSource.CloseReason.PeerClosed;
                return; // The peer closed cleanly between frames.
            }

            switch (frame.Value.Type)
            {
                case (ProtocolMessageType)DocumentProtocolMessageType.Execute:
                case (ProtocolMessageType)DocumentProtocolMessageType.Command:
                    // Executions run on the session lifetime token, not the soft-stop
                    // token: a drain lets in-flight statements finish.
                    await ExecuteAsync(frame.Value, _lifetimeSource.Token).ConfigureAwait(false);
                    break;

                case ProtocolMessageType.Ping:
                    await WriteFrameAsync(ProtocolMessageType.Pong, ReadOnlyMemory<byte>.Empty, _lifetimeSource.Token).ConfigureAwait(false);
                    break;

                case ProtocolMessageType.Terminate:
                    _closeReason = DocumentDatabaseEventSource.CloseReason.Terminated;
                    return;

                default:
                    await RejectUnexpectedFrameAsync(frame.Value.Type).ConfigureAwait(false);
                    return;
            }
        }
    }

    private async Task ExecuteAsync(ProtocolFrame frame, CancellationToken cancellationToken)
    {
        // Decode before the statement boundary: malformed wire input terminates the session.
        DocumentProtocolExecuteMessage? execute = frame.Type == (ProtocolMessageType)DocumentProtocolMessageType.Execute
            ? DocumentProtocolExecuteMessage.Decode(frame.Payload.Span) : null;
        DocumentProtocolCommandMessage? command = execute is null ? DocumentProtocolCommandMessage.Decode(frame.Payload.Span) : null;
        IReadOnlyList<Diagnostic> diagnostics = [];
        try
        {
            if (execute is not null)
            {
                using var parametersJson = JsonDocument.Parse(execute.Parameters, new JsonDocumentOptions { MaxDepth = 256 });
                var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var property in parametersJson.RootElement.EnumerateObject())
                {
                    if (!parameters.TryAdd(property.Name, property.Value.Clone()))
                    {
                        throw new DatabaseException(ErrorMessages.ParameterNameDuplicate);
                    }
                }
                bool wholeDocuments = false;
                var result = await _databaseSession!.ExecuteStatementAsync(() =>
                {
                    var statement = (OqlQueryStatement)new OqlQueryParser().Parse(execute.Statement);
                    diagnostics = statement.Diagnostics.ToArray();
                    if (string.IsNullOrWhiteSpace(execute.Statement) || diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
                    {
                        string detail = string.Join("; ", diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => $"{d.Code}: {d.Message}"));
                        var error = new DatabaseParseException(detail.Length == 0 ? ErrorMessages.StatementInvalid : detail);
                        DocumentDatabaseEventSource.Log.StatementParseFailed(this, _databaseSession!.Database.Name, error);
                        throw error;
                    }
                    wholeDocuments = statement.OqlExpression is OqlSelectExpression { Projections.Count: 1 } select &&
                        select.Projections[0].Expression is OqlStarExpression;
                    return new DocumentQueryRequest(statement, parameters);
                }, cancellationToken).ConfigureAwait(false);
                if (result.Status != DatabaseResultStatus.Success) { diagnostics = result.Diagnostics ?? []; }
                await WriteResultAsync(result, wholeDocuments, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await RunCommandAsync(command!, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is DatabaseException or DatabaseTypeException or DocumentCatalogException or StorageException or JsonException)
        {
            ProtocolErrorCode code = exception is DatabaseParseException ? ProtocolErrorCode.ParseFailure :
                exception is DatabaseOfflineException ? ProtocolErrorCode.Unavailable : ProtocolErrorCode.ExecutionFailure;
            if (exception is DatabaseOfflineException offline)
            {
                diagnostics = [new Diagnostic { Code = offline.Code, Message = offline.Message, Severity = DiagnosticSeverity.Error }];
            }
            else if (exception is DatabaseTransactionCommitUnconfirmedException)
            {
                diagnostics = [new Diagnostic { Code = DocumentDatabase.OfflineCode, Message = exception.Message, Severity = DiagnosticSeverity.Error }];
            }
            await WriteFrameAsync((ProtocolMessageType)DocumentProtocolMessageType.Failure,
                CreateFailure(code, exception.Message, diagnostics).Encode(), cancellationToken).ConfigureAwait(false);
            return;
        }
    }

    private static DocumentProtocolFailureMessage CreateFailure(ProtocolErrorCode code, string message, IReadOnlyList<Diagnostic> diagnostics)
    {
        // A rejected identifier can fill the request frame; repeating it in an error would exceed
        // the response limit. Bound text and diagnostic cardinality before encoding. Even worst-case
        // UTF-8 expansion stays below 5 MiB, preserving a terminal, reusable response.
        const int maximumDiagnostics = 256;
        var bounded = diagnostics.Take(maximumDiagnostics).Select(diagnostic => new Diagnostic
        {
            Code = diagnostic.Code is null ? null : BoundFailureText(diagnostic.Code, 256),
            Message = diagnostic.Message is null ? null : BoundFailureText(diagnostic.Message, 4096),
            Start = diagnostic.Start, End = diagnostic.End, Line = diagnostic.Line,
            Severity = diagnostic.Severity, Location = diagnostic.Location,
        }).ToArray();
        message = BoundFailureText(message, 4096);
        if (diagnostics.Count > maximumDiagnostics) { message += ErrorMessages.DiagnosticsOmitted; }
        return new(code, message, bounded);
    }

    private static string BoundFailureText(string value, int limit)
    {
        if (value.Length <= limit) { return value; }
        // Do not split a UTF-16 surrogate pair at the truncation boundary.
        if (char.IsHighSurrogate(value[limit - 1])) { limit--; }
        return value[..limit] + ErrorMessages.DiagnosticTruncated;
    }

    private async Task WriteResultAsync(DatabaseResult result, bool wholeDocuments, CancellationToken cancellationToken)
    {
        long count = 0;
        if (result is DatabaseResultSet set)
        {
            await using (set.ConfigureAwait(false))
            {
                using var json = new DocumentJsonValues();
                await WriteResultFrameAsync(DocumentProtocolMessageType.ResultHeader,
                    new DocumentProtocolResultHeaderMessage(set.Columns.Select(column => (column.Name, (byte)column.Type)).ToArray(), wholeDocuments).Encode(), cancellationToken).ConfigureAwait(false);
                await foreach (DatabaseRow row in set.GetRowsAsync(cancellationToken).ConfigureAwait(false))
                {
                    var values = new object?[row.FieldCount];
                    for (int index = 0; index < values.Length; index++) { values[index] = row.GetValue(index); }
                    await WriteResultFrameAsync(wholeDocuments ? DocumentProtocolMessageType.Document : DocumentProtocolMessageType.ResultRow,
                        json.Result(values, wholeDocuments), cancellationToken).ConfigureAwait(false);
                    count++;
                }
            }
        }
        else if (result.Status != DatabaseResultStatus.Success)
        {
            throw new DatabaseException(ErrorMessages.StatementFailed);
        }
        await WriteResultFrameAsync(DocumentProtocolMessageType.Complete, new DocumentProtocolCompleteMessage(count, result.AffectedCount).Encode(), cancellationToken).ConfigureAwait(false);
    }

    private async Task RunCommandAsync(DocumentProtocolCommandMessage command, CancellationToken cancellationToken)
    {
        var session = _databaseSession!;
        long count = 0;
        long affected = -1;
        switch (command.Operation)
        {
            case DocumentProtocolOperation.Get:
            case DocumentProtocolOperation.Put:
            case DocumentProtocolOperation.Delete:
                if (command.Operation == DocumentProtocolOperation.Put)
                {
                    // A response too large for the frame must never be discovered after a write.
                    _ = ResultFrame(DocumentProtocolMessageType.StoredDocument,
                        new DocumentProtocolStoredDocumentMessage(new Document(command.Id, new DocumentVersion(0), command.Content)).Encode());
                }
                var collection = await session.GetCollectionAsync(command.Collection, cancellationToken).ConfigureAwait(false);
                Document? document;
                if (command.Operation == DocumentProtocolOperation.Get)
                {
                    document = await collection.GetAsync(session, command.Id, cancellationToken).ConfigureAwait(false);
                }
                else if (command.Operation == DocumentProtocolOperation.Put)
                {
                    document = await collection.PutAsync(session, command.Id, command.Content, command.ExpectedVersion, cancellationToken).ConfigureAwait(false);
                    affected = 1;
                }
                else
                {
                    affected = await collection.DeleteAsync(session, command.Id, command.ExpectedVersion, cancellationToken).ConfigureAwait(false) ? 1 : 0;
                    document = null;
                }
                if (document is { } stored)
                {
                    await WriteResultFrameAsync(DocumentProtocolMessageType.StoredDocument, new DocumentProtocolStoredDocumentMessage(stored).Encode(), cancellationToken).ConfigureAwait(false);
                    count++;
                }
                break;
            case DocumentProtocolOperation.CreateCollection:
                var created = await session.CreateCollectionAsync(command.Collection, cancellationToken).ConfigureAwait(false);
                await WriteResultFrameAsync(DocumentProtocolMessageType.Collection, new DocumentProtocolCollectionMessage(created.Name).Encode(), cancellationToken).ConfigureAwait(false);
                count++;
                affected = 0;
                break;
            case DocumentProtocolOperation.GetCollection:
                var found = await session.GetCollectionAsync(command.Collection, cancellationToken).ConfigureAwait(false);
                await WriteResultFrameAsync(DocumentProtocolMessageType.Collection, new DocumentProtocolCollectionMessage(found.Name).Encode(), cancellationToken).ConfigureAwait(false);
                count++;
                break;
            case DocumentProtocolOperation.DropCollection:
                await session.DropCollectionAsync(command.Collection, cancellationToken).ConfigureAwait(false);
                affected = 0;
                break;
            case DocumentProtocolOperation.ListCollections:
                await foreach (var item in session.GetCollectionsAsync(cancellationToken).ConfigureAwait(false))
                {
                    await WriteResultFrameAsync(DocumentProtocolMessageType.Collection, new DocumentProtocolCollectionMessage(item.Name).Encode(), cancellationToken).ConfigureAwait(false);
                    count++;
                }
                break;
        }
        await WriteResultFrameAsync(DocumentProtocolMessageType.Complete, new DocumentProtocolCompleteMessage(count, affected).Encode(), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteResultFrameAsync(DocumentProtocolMessageType type, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var frame = ResultFrame(type, payload);
        await WriteFrameAsync(frame.Type, frame.Payload, cancellationToken).ConfigureAwait(false);
    }

    private static ProtocolFrame ResultFrame(DocumentProtocolMessageType type, ReadOnlyMemory<byte> payload)
    {
        if ((uint)payload.Length > ProtocolFrameHeader.MaxPayloadLength)
        {
            throw new DatabaseException(ErrorMessages.ResultTooLarge);
        }
        return new ProtocolFrame((ProtocolMessageType)type, payload);
    }


    /// <summary>
    /// Resolves the startup-requested database on the server's one engine:
    /// already-open databases first, then an open attempt.
    /// </summary>
    private async ValueTask<DocumentDatabase?> ResolveDatabaseAsync(string name, CancellationToken cancellationToken)
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

    private async ValueTask WriteFrameAsync(ProtocolMessageType type, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        await _writer!.WriteFrameAsync(new ProtocolFrame(type, payload), cancellationToken).ConfigureAwait(false);
        await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private ValueTask WriteErrorAsync(ProtocolErrorCode code, string message, CancellationToken cancellationToken)
        => WriteFrameAsync(ProtocolMessageType.Error, new ProtocolErrorMessage(code, BoundFailureText(message, 4096)).Encode(), cancellationToken);

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
                _terminalDrainSource = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeSource.Token);
                _terminalDrainRegistration = _terminalDrainSource.Token.Register(static state => ((DocumentDatabaseServerSession)state!).Abort(), this);
                _terminalDrainSource.CancelAfter(_options.ShutdownDrainTimeout);
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
        _closeReason = DocumentDatabaseEventSource.CloseReason.HandshakeRefused;
        DocumentDatabaseEventSource.Log.HandshakeRefused(this, database, principal, code, detail);
        return TryWriteErrorAsync(code, detail);
    }

    /// <summary>
    /// Records that the handshake outlived the authentication timeout, which drops the connection
    /// without an error frame.
    /// </summary>
    private void ReportHandshakeTimeout()
    {
        _closeReason = DocumentDatabaseEventSource.CloseReason.HandshakeTimedOut;
        DocumentDatabaseEventSource.Log.HandshakeTimedOut(this, _options.AuthenticationTimeout);
    }

    /// <summary>
    /// Terminates the session on a frame the ready state does not accept: records the violation
    /// and sends the error frame (best effort).
    /// </summary>
    private ValueTask RejectUnexpectedFrameAsync(ProtocolMessageType type)
    {
        string violation = string.Format(ErrorMessages.ReadyFrameUnexpected, type);
        _closeReason = DocumentDatabaseEventSource.CloseReason.ProtocolViolation;
        DocumentDatabaseEventSource.Log.SessionProtocolViolation(this, violation);
        return TryWriteErrorAsync(ProtocolErrorCode.ProtocolViolation, violation);
    }

    /// <summary>
    /// Classifies a failure that reached the pump as the transport's, and names the close reason it
    /// gives the session; null for anything else, which stays a fault. The five model servers carry
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
        ConnectionAbortedException => DocumentDatabaseEventSource.CloseReason.ConnectionAborted,
        ConnectionException => DocumentDatabaseEventSource.CloseReason.TransportFailed,
        SocketException => DocumentDatabaseEventSource.CloseReason.TransportFailed,
        IOException { InnerException: SocketException or ConnectionException } => DocumentDatabaseEventSource.CloseReason.TransportFailed,
        _ => null,
    };

    private async ValueTask DrainTerminalResponseAsync(CancellationToken softStop)
    {
        if (!_terminalResponseWritten) { return; }
        try
        {
            // Flush queues bytes in a transport pipe. Completing output lets its send pump
            // drain them before disposal can abort it. A peer that keeps its half open is
            // bounded by the deadline already running since before the terminal write. Keep
            // the abort registrations while joining completion: cancellation must abort the
            // transport, not abandon a pending operation that still owns its output.
            using var stopRegistration = softStop.Register(static state => ((CancellationTokenSource)state!).Cancel(), _terminalDrainSource!);
            await _connection.Output.CompleteAsync().ConfigureAwait(false);
            await Task.Delay(Timeout.InfiniteTimeSpan, _terminalDrainSource!.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            DocumentDatabaseEventSource.Log.SessionCleanupFailed(this, exception);
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
                    DocumentDatabaseEventSource.Log.SessionCleanupFailed(this, exception);
                }
            }
        }
        finally
        {
            _lifetimeSource.Dispose();
            _server.OnSessionCompleted(this);
        }
    }
}
