using System;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Security;

using Assimalign.Cohesion.Database.Blob.Protocol;

namespace Assimalign.Cohesion.Database.Blob.Internal;

/// <summary>
/// One server-side session pump: drives the protocol state machine
/// (connected → startup → authenticating → ready ⇄ executing → terminated)
/// over a single connection and routes all requests through the bound Blob session.
/// </summary>
/// <remarks>
/// An internal sealed leaf of the root <see cref="DatabaseServerSession"/> (concrete-types plan,
/// row 11): the base owns the identity, and the negotiated version and authenticated principal,
/// which the handshake records once each; the engine session is re-exposed typed by a covariant
/// override. Option B (concrete-types plan, section 6.6): the exchanges run the container
/// operations of the bound session itself, so they join a transaction the session holds.
/// </remarks>
internal sealed class BlobDatabaseServerSession : DatabaseServerSession
{
    private readonly BlobDatabaseServer _server;
    private readonly IDuplexConnection _connection;
    private readonly BlobDatabaseServerOptions _options;
    private readonly BlobDatabaseEngine _engine;
    private readonly DatabaseAuthenticator _authenticator;
    private readonly CancellationTokenSource _lifetimeSource;
    private readonly long _acceptedTimestamp;

    private ProtocolChannel? _channel;
    private ProtocolFrameReader? _reader;
    private ProtocolFrameWriter? _writer;
    private BlobDatabaseSession? _databaseSession;
    private bool _terminalResponseWritten;
    private CancellationTokenSource? _terminalDrainSource;
    private CancellationTokenRegistration _terminalDrainRegistration;
    private Task _completion = Task.CompletedTask;
    private string _closeReason = BlobDatabaseEventSource.CloseReason.Unknown;

    // Set by Abort before it cancels the lifetime, so the pump can tell the server's own teardown
    // from a transport that closed under it.
    private int _abortRequested;

    internal BlobDatabaseServerSession(
        BlobDatabaseServer server,
        IDuplexConnection connection,
        BlobDatabaseServerOptions options,
        BlobDatabaseEngine engine,
        DatabaseAuthenticator authenticator)
    {
        _server = server;
        _connection = connection;
        _options = options;
        _engine = engine;
        _authenticator = authenticator;
        _lifetimeSource = CancellationTokenSource.CreateLinkedTokenSource(connection.ConnectionClosed);
        _acceptedTimestamp = BlobDatabaseEventSource.Log.SessionTimestamp();
    }

    /// <inheritdoc />
    public override BlobDatabaseSession? DatabaseSession => _databaseSession;

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
        // Claim abort before cancelling the lifetime: the terminal deadline links back here.
        // No lock is held across cancellation callbacks or transport I/O.
        if (Interlocked.Exchange(ref _abortRequested, 1) != 0) { return; }
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

        CancellationTokenRegistration abortRegistration = hardAbort.Register(static state => ((BlobDatabaseServerSession)state!).Abort(), this);

        try
        {
            Stream stream = _connection.AsStream();
            _channel = new ProtocolChannel(stream, BlobProtocol.Family, leaveOpen: true);
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
            _closeReason = BlobDatabaseEventSource.CloseReason.ProtocolViolation;
            BlobDatabaseEventSource.Log.SessionProtocolViolation(this, exception.Message);
            await TryWriteErrorAsync(ProtocolErrorCode.ProtocolViolation, exception.Message).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ClosedUnderPump(softStop, hardAbort))
        {
            // The transport closed under a blocked read or write: a peer that reset or hung up. The
            // session's lifetime follows the connection's ConnectionClosed, which a duplex transport
            // signals as soon as it sees the close (#1459), so the in-flight operation ends in a
            // cancellation the server did not request. That is a transport outcome (plan D9).
            _closeReason = BlobDatabaseEventSource.CloseReason.TransportFailed;
        }
        catch (OperationCanceledException) when (softStop.IsCancellationRequested && !_lifetimeSource.IsCancellationRequested && _databaseSession is not null)
        {
            // Ready can reach the peer before its explicit flush resumes. If soft stop
            // cancels that final handshake flush, the authenticated peer still needs its
            // terminal notice, just as one already inside the ready loop does.
            _closeReason = BlobDatabaseEventSource.CloseReason.Shutdown;
            await TryWriteErrorAsync(ProtocolErrorCode.Unavailable, "The server is shutting down.").ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The server stopped or aborted the session mid-frame.
            _closeReason = BlobDatabaseEventSource.CloseReason.Cancelled;
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
            _closeReason = BlobDatabaseEventSource.CloseReason.Faulted;
            BlobDatabaseEventSource.Log.SessionFaulted(this, exception);
            await TryWriteErrorAsync(ProtocolErrorCode.Internal, "An unexpected server error terminated the session.").ConfigureAwait(false);
        }
        finally
        {
            await DrainTerminalResponseAsync().ConfigureAwait(false);
            // A write can resume inline before the deadline's abort callback runs.
            if (_terminalDrainSource?.IsCancellationRequested == true) { Abort(); }
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
                _closeReason = BlobDatabaseEventSource.CloseReason.PeerClosed;
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

            // Only a disposed engine, or one failed as a whole, refuses every database here; a worker's
            // failure of one database refuses that database after authentication (owner decision 42).
            if (_engine.RefusesEveryDatabase(out var state))
            {
                BlobDatabaseEventSource.Log.EngineRefused(_engine, BlobDatabaseEventSource.RefusalPhase.Handshake, state);
                await RefuseHandshakeAsync(ProtocolErrorCode.Unavailable, $"The Blob engine is {state}.", startup.Database, startup.Principal).ConfigureAwait(false);
                return false;
            }

            BlobDatabase? database = await ResolveDatabaseAsync(startup.Database, handshakeSource.Token).ConfigureAwait(false);

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
                _closeReason = BlobDatabaseEventSource.CloseReason.PeerClosed;
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

            // A database a worker of the engine is failing on is refused, with its code, until the
            // failure ends or the engine gives up on it; the engine's other databases are served
            // (owner decision 42). Checked after authentication, as the offline refusal is, so an
            // unauthenticated peer learns nothing of a database's health.
            if (database.GetWorkerFailureRefusal() is { } failing)
            {
                BlobDatabaseEventSource.Log.DatabaseRefused(this, database.Name, BlobDatabaseEventSource.RefusalPhase.Handshake, failing);
                await RefuseHandshakeAsync(ProtocolErrorCode.Unavailable, failing, startup.Database, startup.Principal).ConfigureAwait(false);
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
                    _closeReason = BlobDatabaseEventSource.CloseReason.Shutdown;
                    await TryWriteErrorAsync(ProtocolErrorCode.Unavailable, "The server is shutting down.").ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (!_lifetimeSource.IsCancellationRequested)
                {
                    // Idle timeout eviction.
                    _closeReason = BlobDatabaseEventSource.CloseReason.IdleTimeout;
                    await TryWriteErrorAsync(ProtocolErrorCode.Unavailable, "The session was closed after exceeding the idle timeout.").ConfigureAwait(false);
                    return;
                }
            }

            if (frame is null)
            {
                _closeReason = BlobDatabaseEventSource.CloseReason.PeerClosed;
                return; // The peer closed cleanly between frames.
            }

            switch (frame.Value.Type)
            {
                case (ProtocolMessageType)BlobProtocolMessageType.Read:
                case (ProtocolMessageType)BlobProtocolMessageType.Write:
                case (ProtocolMessageType)BlobProtocolMessageType.Delete:
                case (ProtocolMessageType)BlobProtocolMessageType.GetProperties:
                case (ProtocolMessageType)BlobProtocolMessageType.List:
                    // Soft stop permits the entire active exchange to finish, including publication.
                    if (!await ExecuteAsync(frame.Value, _lifetimeSource.Token).ConfigureAwait(false))
                    {
                        return;
                    }
                    break;

                case ProtocolMessageType.Ping:
                    await WriteFrameAsync(ProtocolMessageType.Pong, ReadOnlyMemory<byte>.Empty, _lifetimeSource.Token).ConfigureAwait(false);
                    break;

                case ProtocolMessageType.Terminate:
                    _closeReason = BlobDatabaseEventSource.CloseReason.Terminated;
                    return;

                default:
                    await RejectUnexpectedFrameAsync(frame.Value.Type).ConfigureAwait(false);
                    return;
            }
        }
    }

    private async Task<bool> ExecuteAsync(ProtocolFrame frame, CancellationToken cancellationToken)
    {
        if (_engine.RefusesEveryDatabase(out var state))
        {
            _closeReason = BlobDatabaseEventSource.CloseReason.ExchangeRefused;
            BlobDatabaseEventSource.Log.EngineRefused(_engine, BlobDatabaseEventSource.RefusalPhase.Exchange, state);
            await RefuseExchangeAsync($"The Blob engine is {state}.", cancellationToken).ConfigureAwait(false);
            return false;
        }

        // The session's database is refused while a worker of the engine is failing on it; an
        // exchange for another database's session is served meanwhile (owner decision 42).
        if (_databaseSession!.Database.GetWorkerFailureRefusal() is { } failing)
        {
            _closeReason = BlobDatabaseEventSource.CloseReason.ExchangeRefused;
            BlobDatabaseEventSource.Log.DatabaseRefused(this, _databaseSession.Database.Name, BlobDatabaseEventSource.RefusalPhase.Exchange, failing);
            await RefuseExchangeAsync(failing, cancellationToken).ConfigureAwait(false);
            return false;
        }

        try
        {
            var session = _databaseSession!;
            switch ((BlobProtocolMessageType)frame.Type)
            {
                case BlobProtocolMessageType.Write:
                    await UploadAsync(session, BlobWriteMessage.Decode(frame.Payload.Span), cancellationToken).ConfigureAwait(false);
                    break;
                case BlobProtocolMessageType.Read:
                    await DownloadAsync(session, BlobReadMessage.Decode(frame.Payload.Span), cancellationToken).ConfigureAwait(false);
                    break;
                case BlobProtocolMessageType.Delete:
                {
                    BlobDeleteMessage request = BlobDeleteMessage.Decode(frame.Payload.Span);
                    BlobContainer container = await session.GetContainerAsync(request.Container, cancellationToken).ConfigureAwait(false);
                    bool deleted = await container.DeleteAsync(request.Name, cancellationToken).ConfigureAwait(false);
                    await CompleteAsync(deleted ? 1 : 0, cancellationToken).ConfigureAwait(false);
                    break;
                }
                case BlobProtocolMessageType.GetProperties:
                {
                    BlobGetPropertiesMessage request = BlobGetPropertiesMessage.Decode(frame.Payload.Span);
                    BlobContainer container = await session.GetContainerAsync(request.Container, cancellationToken).ConfigureAwait(false);
                    BlobProperties? properties = await container.GetPropertiesAsync(request.Name, cancellationToken).ConfigureAwait(false);
                    if (properties is { } found)
                    {
                        await PropertiesAsync(found, cancellationToken).ConfigureAwait(false);
                    }
                    await CompleteAsync(properties is null ? 0 : 1, cancellationToken).ConfigureAwait(false);
                    break;
                }
                case BlobProtocolMessageType.List:
                {
                    BlobListMessage request = BlobListMessage.Decode(frame.Payload.Span);
                    BlobContainer container = await session.GetContainerAsync(request.Container, cancellationToken).ConfigureAwait(false);
                    long count = 0;
                    await foreach (BlobProperties properties in container.GetBlobsAsync(request.Prefix, cancellationToken).ConfigureAwait(false))
                    {
                        await PropertiesAsync(properties, cancellationToken).ConfigureAwait(false);
                        count = checked(count + 1);
                    }
                    await CompleteAsync(count, cancellationToken).ConfigureAwait(false);
                    break;
                }
            }
            return true;
        }
        catch (Exception exception) when (exception is not (ProtocolException or OperationCanceledException or OutOfMemoryException))
        {
            // A transfer may already be in progress. An error is terminal: no uncertain frame
            // boundary or partially consumed content is returned to the connection pool. The
            // teardown ends the session's transaction; a host-opened one is aborted first, so the
            // host's commit names this failure whenever it runs (#1225).
            // An offline database (#1243) refuses every exchange with its coded reason.
            var code = exception is DatabaseOfflineException ? ProtocolErrorCode.Unavailable : ProtocolErrorCode.ExecutionFailure;
            if (TransportCloseReason(exception) is { } transportReason)
            {
                // The transport failed under the exchange: a peer that hung up or reset the
                // connection, or the shutdown's abort, which SessionsAborted reports. An expected
                // outcome, which SessionClosed's reason carries as the pump's own catches do (plan
                // D9), classified by the pump's own rule. A bare IOException is still reported: it
                // can be the storage device's, not the peer's.
                _closeReason = transportReason;
            }
            else
            {
                _closeReason = BlobDatabaseEventSource.CloseReason.ExchangeFailed;
                if (BlobDatabaseEventSource.Log.IsEnabled(EventLevel.Error, EventKeywords.None))
                {
                    // The request is read back from its frame only while the event is on, and in a
                    // synchronous helper, so the exchange's state machine gains no field.
                    ReportTransferFailed(frame, code, exception);
                }
            }

            await AbortHostTransactionAsync(exception).ConfigureAwait(false);
            var diagnostic = (exception as DatabaseException)?.Diagnostics.FirstOrDefault(item => item.Severity == Types.DiagnosticSeverity.Error);
            string errorText = diagnostic?.Code is { } diagnosticCode && !exception.Message.StartsWith(diagnosticCode + ":", StringComparison.Ordinal)
                ? $"{diagnosticCode}: {exception.Message}" : exception.Message;
            await TryWriteErrorAsync(code, errorText).ConfigureAwait(false);
            return false;
        }
    }

    // An exchange refused before it starts (an engine that refuses every database, a database a
    // worker is failing on) is terminal like every wire failure, so it ends a host-opened
    // transaction too: aborted with the refusal before the error is written, so the host's commit
    // names the refusal whichever of the commit and the teardown runs first (#1225; owner decision
    // 42 review).
    private async Task RefuseExchangeAsync(string message, CancellationToken cancellationToken)
    {
        await AbortHostTransactionAsync(new DatabaseException(message)).ConfigureAwait(false);
        await WriteErrorAsync(ProtocolErrorCode.Unavailable, message, cancellationToken).ConfigureAwait(false);
    }

    // Aborts the session's still-usable host transaction with the failure the client is told about.
    private async Task AbortHostTransactionAsync(Exception cause)
    {
        if (_databaseSession is { } session)
        {
            try
            {
                await session.AbortTransactionAsync(cause).ConfigureAwait(false);
            }
            catch (Exception abortError) when (abortError is not OutOfMemoryException)
            {
                // The transaction stays Faulted with the rollback failure recorded, and the
                // teardown retries the rollback; the client still gets the original failure.
                BlobDatabaseEventSource.Log.HostTransactionAbortFailed(this, abortError);
            }
        }
    }

    private async Task UploadAsync(BlobDatabaseSession session, BlobWriteMessage request, CancellationToken cancellationToken)
    {
        ProtocolFrame? start = await _reader!.ReadFrameAsync(cancellationToken).ConfigureAwait(false);
        if (start is null || start.Value.Type != (ProtocolMessageType)BlobProtocolMessageType.TransferStart)
        {
            throw new ProtocolException("A Blob upload must begin with TransferStart.");
        }
        BlobTransferStartMessage metadata = BlobTransferStartMessage.Decode(start.Value.Payload.Span);
        await using BlobDatabaseTransaction transaction = await session.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        Stream? destination = null;
        try
        {
            BlobContainer container = await session.GetContainerAsync(request.Container, cancellationToken).ConfigureAwait(false);
            destination = await container.OpenWriteAsync(request.Name, new BlobWriteOptions
            {
                ContentType = metadata.ContentType.Length == 0 ? null : metadata.ContentType,
                Overwrite = request.Overwrite
            }, cancellationToken).ConfigureAwait(false);
            BlobTransferStartMessage received = await BlobProtocolTransfer.ReceiveAsync(
                _reader, _writer!, destination, metadata, cancellationToken).ConfigureAwait(false);
            await destination.DisposeAsync().ConfigureAwait(false);
            destination = null;
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            await WriteFrameAsync((ProtocolMessageType)BlobProtocolMessageType.TransferComplete,
                new BlobTransferCompleteMessage(received.Length).Encode(), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Roll back BEFORE successful disposal can finalize the storage stream. Explicit
            // transactions keep even successfully disposed destinations invisible until commit.
            // A failed operation leaves the transaction Faulted until it is rolled back (#1225);
            // only a committed transaction refuses the rollback.
            if (transaction.State is TransactionState.Active or TransactionState.Faulted)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            throw;
        }
        finally
        {
            if (destination is not null)
            {
                try { await destination.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) when (exception is not OutOfMemoryException) { }
            }
        }
    }

    private async Task DownloadAsync(BlobDatabaseSession session, BlobReadMessage request, CancellationToken cancellationToken)
    {
        // Metadata and content must refer to the same immutable version even when replaced
        // concurrently. The read-only transaction keeps its snapshot until stream disposal.
        await using BlobDatabaseTransaction transaction = await session.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        BlobContainer container = await session.GetContainerAsync(request.Container, cancellationToken).ConfigureAwait(false);
        BlobProperties properties = await container.GetPropertiesAsync(request.Name, cancellationToken).ConfigureAwait(false)
            ?? throw new DatabaseException($"Blob '{request.Name}' does not exist.");
        await using Stream source = await container.OpenReadAsync(request.Name, cancellationToken).ConfigureAwait(false);
        await BlobProtocolTransfer.SendAsync(_reader!, _writer!, source,
            new BlobTransferStartMessage(properties.Length, properties.ContentType ?? ""), cancellationToken).ConfigureAwait(false);
    }

    private ValueTask PropertiesAsync(BlobProperties properties, CancellationToken cancellationToken)
        => WriteFrameAsync((ProtocolMessageType)BlobProtocolMessageType.Properties,
            new BlobPropertiesMessage(properties).Encode(), cancellationToken);

    private ValueTask CompleteAsync(long count, CancellationToken cancellationToken)
        => WriteFrameAsync((ProtocolMessageType)BlobProtocolMessageType.OperationComplete,
            new BlobOperationCompleteMessage(count).Encode(), cancellationToken);

    /// <summary>
    /// Resolves the startup-requested database on the server's one engine:
    /// already-open databases first, then an open attempt.
    /// </summary>
    private async ValueTask<BlobDatabase?> ResolveDatabaseAsync(string name, CancellationToken cancellationToken)
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
                _terminalDrainSource = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeSource.Token);
                _terminalDrainRegistration = _terminalDrainSource.Token.Register(static state => ((BlobDatabaseServerSession)state!).Abort(), this);
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
        _closeReason = BlobDatabaseEventSource.CloseReason.HandshakeRefused;
        BlobDatabaseEventSource.Log.HandshakeRefused(this, database, principal, code, detail);
        return TryWriteErrorAsync(code, detail);
    }

    /// <summary>
    /// Records that the handshake outlived the authentication timeout, which drops the connection
    /// without an error frame.
    /// </summary>
    private void ReportHandshakeTimeout()
    {
        _closeReason = BlobDatabaseEventSource.CloseReason.HandshakeTimedOut;
        BlobDatabaseEventSource.Log.HandshakeTimedOut(this, _options.AuthenticationTimeout);
    }

    /// <summary>
    /// Terminates the session on a frame the ready state does not accept: records the violation
    /// and sends the error frame (best effort).
    /// </summary>
    private ValueTask RejectUnexpectedFrameAsync(ProtocolMessageType type)
    {
        string violation = $"Unexpected {type} frame in the ready state.";
        _closeReason = BlobDatabaseEventSource.CloseReason.ProtocolViolation;
        BlobDatabaseEventSource.Log.SessionProtocolViolation(this, violation);
        return TryWriteErrorAsync(ProtocolErrorCode.ProtocolViolation, violation);
    }

    /// <summary>
    /// Writes a failed exchange's <c>TransferFailed</c> event with the container its request named
    /// and the code of the error frame the session sends; the blob's name is never read.
    /// </summary>
    private void ReportTransferFailed(ProtocolFrame frame, ProtocolErrorCode code, Exception exception)
        => BlobDatabaseEventSource.Log.TransferFailed(this, DescribeContainer(frame), code, exception);

    /// <summary>
    /// Reads the container a failed exchange named from the request frame the exchange decoded:
    /// the exchange keeps no copy of its own. A frame that no longer decodes gives an empty name
    /// rather than a second failure.
    /// </summary>
    private static string DescribeContainer(ProtocolFrame frame)
    {
        try
        {
            return (BlobProtocolMessageType)frame.Type switch
            {
                BlobProtocolMessageType.Write => BlobWriteMessage.Decode(frame.Payload.Span).Container,
                BlobProtocolMessageType.Read => BlobReadMessage.Decode(frame.Payload.Span).Container,
                BlobProtocolMessageType.Delete => BlobDeleteMessage.Decode(frame.Payload.Span).Container,
                BlobProtocolMessageType.GetProperties => BlobGetPropertiesMessage.Decode(frame.Payload.Span).Container,
                BlobProtocolMessageType.List => BlobListMessage.Decode(frame.Payload.Span).Container,
                _ => string.Empty,
            };
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return string.Empty;
        }
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
        ConnectionAbortedException => BlobDatabaseEventSource.CloseReason.ConnectionAborted,
        ConnectionException => BlobDatabaseEventSource.CloseReason.TransportFailed,
        SocketException => BlobDatabaseEventSource.CloseReason.TransportFailed,
        IOException { InnerException: SocketException or ConnectionException } => BlobDatabaseEventSource.CloseReason.TransportFailed,
        _ => null,
    };

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
            // The frame reader has exited. Consume pending peer input so receive
            // back-pressure cannot hide the peer's close. Soft stop triggers the notice;
            // only hard abort or the terminal deadline cuts its delivery short.
            while (true)
            {
                var result = await _connection.Input.ReadAsync(_terminalDrainSource!.Token).ConfigureAwait(false);
                _connection.Input.AdvanceTo(result.Buffer.End);
                if (result.IsCompleted || result.IsCanceled) { break; }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (TransportCloseReason(exception) is not null)
        {
            // A terminal peer can reset or abort after consuming its error frame.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            BlobDatabaseEventSource.Log.SessionCleanupFailed(this, exception);
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
                    BlobDatabaseEventSource.Log.SessionCleanupFailed(this, exception);
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
