using System;
using System.Diagnostics.Tracing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Security;

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
    private readonly IConnection _connection;
    private readonly BlobDatabaseServerOptions _options;
    private readonly BlobDatabaseEngine _engine;
    private readonly DatabaseAuthenticator _authenticator;
    private readonly CancellationTokenSource _lifetimeSource;
    private readonly long _acceptedTimestamp;

    private ProtocolChannel? _channel;
    private ProtocolFrameReader? _reader;
    private ProtocolFrameWriter? _writer;
    private BlobDatabaseSession? _databaseSession;
    private Task _completion = Task.CompletedTask;
    private string _closeReason = BlobDatabaseEventSource.CloseReason.Unknown;

    internal BlobDatabaseServerSession(
        BlobDatabaseServer server,
        IConnection connection,
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
        catch (OperationCanceledException)
        {
            // Aborted, connection closed, or stop signaled mid-frame.
            _closeReason = BlobDatabaseEventSource.CloseReason.Canceled;
        }
        catch (ConnectionAbortedException)
        {
            _closeReason = BlobDatabaseEventSource.CloseReason.ConnectionAborted;
        }
        catch (IOException)
        {
            // The transport failed under the pump; nothing to report to the peer.
            _closeReason = BlobDatabaseEventSource.CloseReason.TransportFailed;
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
            if (exception is ConnectionException)
            {
                // The connection was aborted or reset under the exchange: a peer that hung up, or
                // the shutdown's abort, which SessionsAborted reports. An expected outcome, which
                // SessionClosed's reason carries as the pump's own catches do (plan D9). An
                // IOException is still reported: it can be the storage device's, not the peer's.
                _closeReason = exception is ConnectionAbortedException
                    ? BlobDatabaseEventSource.CloseReason.ConnectionAborted
                    : BlobDatabaseEventSource.CloseReason.TransportFailed;
            }
            else
            {
                _closeReason = BlobDatabaseEventSource.CloseReason.ExchangeFailed;
                if (BlobDatabaseEventSource.Log.IsEnabled(EventLevel.Warning, EventKeywords.None))
                {
                    // The request is read back from its frame only while the event is on, and in a
                    // synchronous helper, so the exchange's state machine gains no field.
                    ReportTransferFailed(frame, exception);
                }
            }

            await AbortHostTransactionAsync(exception).ConfigureAwait(false);
            // An offline database (#1243) refuses every exchange with its coded reason.
            var code = exception is DatabaseOfflineException ? ProtocolErrorCode.Unavailable : ProtocolErrorCode.ExecutionFailure;
            await TryWriteErrorAsync(code, exception.Message).ConfigureAwait(false);
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
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await WriteErrorAsync(code, message, timeout.Token).ConfigureAwait(false);
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
    /// Writes a failed exchange's <c>TransferFailed</c> event with the container and the blob (a
    /// list's prefix) its request named: the event writes the container and redacts the blob's
    /// quoted name from the failure's message.
    /// </summary>
    private void ReportTransferFailed(ProtocolFrame frame, Exception exception)
    {
        (string container, string blob) = DescribeRequest(frame);
        BlobDatabaseEventSource.Log.TransferFailed(this, container, blob, exception);
    }

    /// <summary>
    /// Reads the container and the blob (a list's prefix) a failed exchange named from the request
    /// frame the exchange decoded: the exchange keeps no copy of its own. A frame that no longer
    /// decodes gives empty names rather than a second failure.
    /// </summary>
    private static (string Container, string Blob) DescribeRequest(ProtocolFrame frame)
    {
        try
        {
            switch ((BlobProtocolMessageType)frame.Type)
            {
                case BlobProtocolMessageType.Write:
                {
                    BlobWriteMessage request = BlobWriteMessage.Decode(frame.Payload.Span);
                    return (request.Container, request.Name);
                }
                case BlobProtocolMessageType.Read:
                {
                    BlobReadMessage request = BlobReadMessage.Decode(frame.Payload.Span);
                    return (request.Container, request.Name);
                }
                case BlobProtocolMessageType.Delete:
                {
                    BlobDeleteMessage request = BlobDeleteMessage.Decode(frame.Payload.Span);
                    return (request.Container, request.Name);
                }
                case BlobProtocolMessageType.GetProperties:
                {
                    BlobGetPropertiesMessage request = BlobGetPropertiesMessage.Decode(frame.Payload.Span);
                    return (request.Container, request.Name);
                }
                case BlobProtocolMessageType.List:
                {
                    BlobListMessage request = BlobListMessage.Decode(frame.Payload.Span);
                    return (request.Container, request.Prefix);
                }
                default:
                    return (string.Empty, string.Empty);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return (string.Empty, string.Empty);
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
