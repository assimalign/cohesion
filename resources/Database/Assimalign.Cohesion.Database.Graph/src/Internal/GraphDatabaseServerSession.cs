using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Security;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Internal;

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
internal sealed class GraphDatabaseServerSession : DatabaseServerSession
{
    private readonly GraphDatabaseServer _server;
    private readonly IDuplexConnection _connection;
    private readonly GraphDatabaseServerOptions _options;
    private readonly GraphDatabaseEngine _engine;
    private readonly DatabaseAuthenticator _authenticator;
    private readonly CancellationTokenSource _lifetimeSource;
    private readonly long _acceptedTimestamp;

    private ProtocolChannel? _channel;
    private ProtocolFrameReader? _reader;
    private ProtocolFrameWriter? _writer;
    private GraphDatabaseSession? _databaseSession;
    private Location? _statementLocation;
    private Task _completion = Task.CompletedTask;
    private string _closeReason = GraphDatabaseEventSource.CloseReason.Unknown;

    // Set by Abort before it cancels the lifetime, so the pump can tell the server's own teardown
    // from a transport that closed under it.
    private bool _abortRequested;

    internal GraphDatabaseServerSession(
        GraphDatabaseServer server,
        IDuplexConnection connection,
        GraphDatabaseServerOptions options,
        GraphDatabaseEngine engine,
        DatabaseAuthenticator authenticator)
    {
        _server = server;
        _connection = connection;
        _options = options;
        _engine = engine;
        _authenticator = authenticator;
        _lifetimeSource = CancellationTokenSource.CreateLinkedTokenSource(connection.ConnectionClosed);
        _acceptedTimestamp = GraphDatabaseEventSource.Log.SessionTimestamp();
    }

    /// <inheritdoc />
    public override GraphDatabaseSession? DatabaseSession => _databaseSession;

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
            Volatile.Write(ref _abortRequested, true);
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

        CancellationTokenRegistration abortRegistration = hardAbort.Register(static state => ((GraphDatabaseServerSession)state!).Abort(), this);

        try
        {
            Stream stream = _connection.AsStream();
            _channel = new ProtocolChannel(stream, GraphProtocol.Family, leaveOpen: true);
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
            _closeReason = GraphDatabaseEventSource.CloseReason.ProtocolViolation;
            GraphDatabaseEventSource.Log.SessionProtocolViolation(this, exception.Message);
            await TryWriteErrorAsync(ProtocolErrorCode.ProtocolViolation, exception.Message).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ClosedUnderPump(softStop, hardAbort))
        {
            // The transport closed under a blocked read or write: a peer that reset or hung up. The
            // session's lifetime follows the connection's ConnectionClosed, which a duplex transport
            // signals as soon as it sees the close (#1459), so the in-flight operation ends in a
            // cancellation the server did not request. That is a transport outcome (plan D9).
            _closeReason = GraphDatabaseEventSource.CloseReason.TransportFailed;
        }
        catch (OperationCanceledException)
        {
            // The server stopped or aborted the session mid-frame.
            _closeReason = GraphDatabaseEventSource.CloseReason.Cancelled;
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
            _closeReason = GraphDatabaseEventSource.CloseReason.Faulted;
            GraphDatabaseEventSource.Log.SessionFaulted(this, exception);
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
                _closeReason = GraphDatabaseEventSource.CloseReason.PeerClosed;
                return false; // The peer closed before starting up.
            }

            if (frame.Value.Type != ProtocolMessageType.Startup)
            {
                await RefuseHandshakeAsync(ProtocolErrorCode.ProtocolViolation, $"Expected a startup frame but received {frame.Value.Type}.", string.Empty, string.Empty).ConfigureAwait(false);
                return false;
            }

            ProtocolStartupMessage startup = ProtocolStartupMessage.Decode(frame.Value.Payload.Span);

            if (!ProtocolVersion.TryNegotiate(startup.Version, out var negotiated))
            {
                await RefuseHandshakeAsync(ProtocolErrorCode.UnsupportedVersion, $"Protocol major version {startup.Version.Major} is not supported; the server speaks {ProtocolVersion.Current}.", startup.Database, startup.Principal).ConfigureAwait(false);
                return false;
            }

            SetNegotiatedVersion(negotiated);

            GraphDatabase? database = await ResolveDatabaseAsync(startup.Database, handshakeSource.Token).ConfigureAwait(false);

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
                _closeReason = GraphDatabaseEventSource.CloseReason.PeerClosed;
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

            await WriteFrameAsync(ProtocolMessageType.Ready, _channel!.Negotiate(negotiated, startup.Capabilities).Encode(), handshakeSource.Token).ConfigureAwait(false);
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
                    _closeReason = GraphDatabaseEventSource.CloseReason.Shutdown;
                    await TryWriteErrorAsync(ProtocolErrorCode.Unavailable, "The server is shutting down.").ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (!_lifetimeSource.IsCancellationRequested)
                {
                    // Idle timeout eviction.
                    _closeReason = GraphDatabaseEventSource.CloseReason.IdleTimeout;
                    await TryWriteErrorAsync(ProtocolErrorCode.Unavailable, "The session was closed after exceeding the idle timeout.").ConfigureAwait(false);
                    return;
                }
            }

            if (frame is null)
            {
                _closeReason = GraphDatabaseEventSource.CloseReason.PeerClosed;
                return; // The peer closed cleanly between frames.
            }

            switch (frame.Value.Type)
            {
                case (ProtocolMessageType)GraphProtocolMessageType.Execute:
                case (ProtocolMessageType)GraphProtocolMessageType.ExecutePaths:
                    // Executions run on the session lifetime token, not the soft-stop
                    // token: a drain lets in-flight statements finish.
                    await ExecuteAsync(frame.Value, rowWriter, _lifetimeSource.Token).ConfigureAwait(false);
                    break;

                case ProtocolMessageType.Ping:
                    await WriteFrameAsync(ProtocolMessageType.Pong, ReadOnlyMemory<byte>.Empty, _lifetimeSource.Token).ConfigureAwait(false);
                    break;

                case ProtocolMessageType.Terminate:
                    _closeReason = GraphDatabaseEventSource.CloseReason.Terminated;
                    return;

                default:
                    await RejectUnexpectedFrameAsync(frame.Value.Type).ConfigureAwait(false);
                    return;
            }
        }
    }

    private async Task ExecuteAsync(ProtocolFrame frame, DatabaseKeyWriter rowWriter, CancellationToken cancellationToken)
    {
        GraphProtocolExecuteMessage message = GraphProtocolExecuteMessage.Decode(frame.Payload.Span);
        // Result-shape validation has no AST of its own; retain this exchange's complete text span.
        _statementLocation = Location.Create(1, 1 + message.Statement.Count(character => character == '\n'), 0, message.Statement.Length);
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

        bool paths = frame.Type == (ProtocolMessageType)GraphProtocolMessageType.ExecutePaths;
        try
        {
            if (string.IsNullOrWhiteSpace(message.Statement))
            {
                // The root never sees an empty wire statement either, so it is reported as a
                // parse failure, as the parse delegates below report theirs.
                var empty = new DatabaseParseException("A graph statement must not be empty.", [new Diagnostic
                {
                    Code = "GQL0001",
                    Message = "A graph statement must not be empty.",
                    Severity = DiagnosticSeverity.Error,
                    Location = DiagnosticLocation.Absolute,
                    Start = _statementLocation.Start,
                    End = _statementLocation.End,
                    Line = 1,
                }]);
                GraphDatabaseEventSource.Log.StatementParseFailed(this, _databaseSession!.Database.Name, empty);
                throw empty;
            }
            // The engine session parses and validates the statement, so a statement that fails
            // here aborts an explicit transaction exactly as one that fails in process (#1188).
            // Negotiated warnings precede scalar or path completion.
            // The parse delegates write StatementParseFailed for a statement that fails to parse:
            // it fails before the root session sees it, so the root's statement events do not.
            var result = paths
                ? await _databaseSession!.ExecuteStatementAsync(
                    () => ParsePathsRequest(message.Statement, parameters), cancellationToken).ConfigureAwait(false)
                : await _databaseSession!.ExecuteStatementAsync(
                    () => ParseScalarRequest(message.Statement, parameters), cancellationToken).ConfigureAwait(false);
            try
            {
                if (paths)
                {
                    await WritePathsAsync(result, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await WriteResultAsync(result, rowWriter, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is DatabaseException or DatabaseTypeException)
            {
                // The client sees this statement fail, so it aborts an explicit transaction as an
                // execution failure does, although its operation already completed. Bolt marks the
                // transaction failed on any failure of the exchange, result streaming included.
                await _databaseSession!.AbortTransactionAsync(exception).ConfigureAwait(false);
                throw;
            }
        }
        catch (DatabaseParseException exception)
        {
            // Statement-level failures keep the session in the ready state.
            var diagnostic = exception.Diagnostics.FirstOrDefault(item => item.Severity == DiagnosticSeverity.Error);
            string errorText = diagnostic?.Code is { } code && !exception.Message.Contains(code + ":", StringComparison.Ordinal)
                ? $"{code}: {exception.Message}" : exception.Message;
            await WriteDiagnosticsAsync(PrepareDiagnostics(exception.Diagnostics), cancellationToken).ConfigureAwait(false);
            await WriteErrorAsync(ProtocolErrorCode.ParseFailure, errorText, cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (DatabaseOfflineException exception)
        {
            // The database is offline (#1243): every statement is refused, with the coded
            // reason, until it is reopened; the session stays ready to report it.
            await WriteDiagnosticsAsync(PrepareDiagnostics(exception.Diagnostics), cancellationToken).ConfigureAwait(false);
            await WriteErrorAsync(ProtocolErrorCode.Unavailable, exception.Message, cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (DatabaseException exception)
        {
            var diagnostic = exception.Diagnostics.FirstOrDefault(item => item.Severity == DiagnosticSeverity.Error);
            string errorText = diagnostic?.Code is { } code ? $"{code}: {exception.Message}" : exception.Message;
            await WriteDiagnosticsAsync(PrepareDiagnostics(exception.Diagnostics), cancellationToken).ConfigureAwait(false);
            await WriteErrorAsync(ProtocolErrorCode.ExecutionFailure, errorText, cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (DatabaseTypeException exception)
        {
            await WriteErrorAsync(ProtocolErrorCode.ExecutionFailure, exception.Message, cancellationToken).ConfigureAwait(false);
        }
    }

    // The Execute exchange carries scalar rows only; entity and path projections use ExecutePaths.
    // A parse or validation failure is written and propagates unchanged. The filter takes the
    // failures ExecuteAsync answers as statement failures; anything else is an unexpected fault,
    // which the pump reports once, as SessionFaulted.
    private GraphQueryRequest ParseScalarRequest(string statement, IReadOnlyDictionary<string, object?>? parameters)
    {
        try
        {
            var request = GraphQueryRequest.FromGql(statement, parameters);
            if (request.Statement.GqlExpression.IsExplain) { return request; }
            foreach (var projection in request.Statement.GqlExpression.Projections)
            {
                if (projection.Property is null)
                {
                    throw new GraphPlanningException("COHDBG001", "Execute accepts scalar property projections. Use ExecutePaths with a read-only MATCH to return a node, relationship, or path.", request.Statement.GqlExpression.Location);
                }
            }
            return request;
        }
        catch (Exception exception) when (exception is DatabaseException or DatabaseTypeException)
        {
            GraphDatabaseEventSource.Log.StatementParseFailed(this, _databaseSession!.Database.Name, exception);
            throw;
        }
    }

    // The ExecutePaths exchange's parse delegate. A parse or validation failure is written and
    // propagates unchanged; the filter is ParseScalarRequest's.
    private GraphPathsQueryRequest ParsePathsRequest(string statement, IReadOnlyDictionary<string, object?>? parameters)
    {
        try
        {
            return GraphPathsQueryRequest.FromGql(statement, parameters);
        }
        catch (Exception exception) when (exception is DatabaseException or DatabaseTypeException)
        {
            GraphDatabaseEventSource.Log.StatementParseFailed(this, _databaseSession!.Database.Name, exception);
            throw;
        }
    }

    private async Task WritePathsAsync(DatabaseResult result, CancellationToken cancellationToken)
    {
        if (result is not GraphPathsQueryResult paths)
        {
            throw new GraphPlanningException("COHDBG001", "A path request did not produce a graph path result.", _statementLocation);
        }
        byte[]? diagnosticPayload = PrepareDiagnostics(paths.Diagnostics);
        foreach (GraphPath path in paths.Paths)
        {
            await WriteFrameAsync((ProtocolMessageType)GraphProtocolMessageType.Path,
                new GraphProtocolPathMessage(path.Nodes, path.Relationships).Encode(), cancellationToken).ConfigureAwait(false);
        }
        await WriteDiagnosticsAsync(diagnosticPayload, cancellationToken).ConfigureAwait(false);
        await WriteFrameAsync((ProtocolMessageType)GraphProtocolMessageType.PathsComplete,
            new GraphProtocolPathsCompleteMessage(paths.Paths.Count).Encode(), cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteResultAsync(DatabaseResult result, DatabaseKeyWriter rowWriter, CancellationToken cancellationToken)
    {
        byte[]? diagnosticPayload = PrepareDiagnostics(result.Diagnostics);
        if (result is DatabaseResultSet resultSet)
        {
            await using (resultSet.ConfigureAwait(false))
            {
                var columns = new List<(string Name, byte Type)>(resultSet.Columns.Count);

                foreach (DatabaseColumn column in resultSet.Columns)
                {
                    columns.Add((column.Name, (byte)column.Type));
                }

                await WriteFrameAsync((ProtocolMessageType)GraphProtocolMessageType.ResultHeader, new GraphProtocolResultHeaderMessage(columns).Encode(), cancellationToken).ConfigureAwait(false);

                await foreach (DatabaseRow row in resultSet.GetRowsAsync(cancellationToken).ConfigureAwait(false))
                {
                    rowWriter.Reset();

                    for (int ordinal = 0; ordinal < row.FieldCount; ordinal++)
                    {
                        DatabaseValueCodec.Append(rowWriter, row.GetValue(ordinal));
                    }

                    await WriteFrameAsync((ProtocolMessageType)GraphProtocolMessageType.ResultRow, rowWriter.ToArray(), cancellationToken).ConfigureAwait(false);
                }

                await WriteDiagnosticsAsync(diagnosticPayload, cancellationToken).ConfigureAwait(false);
                await WriteFrameAsync((ProtocolMessageType)GraphProtocolMessageType.ResultComplete, new GraphProtocolResultCompleteMessage(resultSet.AffectedCount).Encode(), cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        if (result.Status != DatabaseResultStatus.Success)
        {
            var diagnostics = result.Diagnostics ?? [];
            string detail = diagnostics.FirstOrDefault(item => item.Severity == DiagnosticSeverity.Error)?.Message
                ?? diagnostics.FirstOrDefault()?.Message
                ?? $"The statement completed with status {result.Status}.";

            // Reported as an ExecutionFailure by the caller, which aborts an explicit transaction first.
            throw new DatabaseException(detail, diagnostics);
        }

        await WriteDiagnosticsAsync(diagnosticPayload, cancellationToken).ConfigureAwait(false);
        await WriteFrameAsync((ProtocolMessageType)GraphProtocolMessageType.ResultComplete, new GraphProtocolResultCompleteMessage(result.AffectedCount).Encode(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the startup-requested database on the server's one engine:
    /// already-open databases first, then an open attempt.
    /// </summary>
    private async ValueTask<GraphDatabase?> ResolveDatabaseAsync(string name, CancellationToken cancellationToken)
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
    private byte[]? PrepareDiagnostics(IReadOnlyList<Diagnostic>? diagnostics)
    {
        if (diagnostics is null || diagnostics.Count == 0 ||
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
                    DiagnosticSize("COHDBP001", notice) <= ProtocolDiagnosticsMessage.MaxPayloadLength - size)
                {
                    entries.Add(new ProtocolDiagnostic(ProtocolDiagnosticSeverity.Warning, "COHDBP001", notice, null, null, 1));
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
        _closeReason = GraphDatabaseEventSource.CloseReason.HandshakeRefused;
        GraphDatabaseEventSource.Log.HandshakeRefused(this, database, principal, code, detail);
        return TryWriteErrorAsync(code, detail);
    }

    /// <summary>
    /// Records that the handshake outlived the authentication timeout, which drops the connection
    /// without an error frame.
    /// </summary>
    private void ReportHandshakeTimeout()
    {
        _closeReason = GraphDatabaseEventSource.CloseReason.HandshakeTimedOut;
        GraphDatabaseEventSource.Log.HandshakeTimedOut(this, _options.AuthenticationTimeout);
    }

    /// <summary>
    /// Terminates the session on a frame the ready state does not accept: records the violation
    /// and sends the error frame (best effort).
    /// </summary>
    private ValueTask RejectUnexpectedFrameAsync(ProtocolMessageType type)
    {
        string violation = $"Unexpected {type} frame in the ready state.";
        _closeReason = GraphDatabaseEventSource.CloseReason.ProtocolViolation;
        GraphDatabaseEventSource.Log.SessionProtocolViolation(this, violation);
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
            && !Volatile.Read(ref _abortRequested)
            && _connection.ConnectionClosed.IsCancellationRequested;

    private static string? TransportCloseReason(Exception exception) => exception switch
    {
        ConnectionAbortedException => GraphDatabaseEventSource.CloseReason.ConnectionAborted,
        ConnectionException => GraphDatabaseEventSource.CloseReason.TransportFailed,
        SocketException => GraphDatabaseEventSource.CloseReason.TransportFailed,
        IOException { InnerException: SocketException or ConnectionException } => GraphDatabaseEventSource.CloseReason.TransportFailed,
        _ => null,
    };

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
                    GraphDatabaseEventSource.Log.SessionCleanupFailed(this, exception);
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
