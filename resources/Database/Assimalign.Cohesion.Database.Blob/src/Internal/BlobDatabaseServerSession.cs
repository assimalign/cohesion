using System;
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
internal sealed class BlobDatabaseServerSession : IDatabaseServerSession
{
    private readonly BlobDatabaseServer _server;
    private readonly IConnection _connection;
    private readonly BlobDatabaseServerOptions _options;
    private readonly IDatabaseEngine _engine;
    private readonly IDatabaseAuthenticator _authenticator;
    private readonly CancellationTokenSource _lifetimeSource;

    private ProtocolChannel? _channel;
    private IProtocolFrameReader? _reader;
    private IProtocolFrameWriter? _writer;
    private IDatabaseSession? _databaseSession;
    private Task _completion = Task.CompletedTask;

    internal BlobDatabaseServerSession(
        BlobDatabaseServer server,
        IConnection connection,
        BlobDatabaseServerOptions options,
        IDatabaseEngine engine,
        IDatabaseAuthenticator authenticator)
    {
        _server = server;
        _connection = connection;
        _options = options;
        _engine = engine;
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

        if (!ProtocolVersion.TryNegotiate(startup.Version, out var negotiated))
        {
            await TryWriteErrorAsync(ProtocolErrorCode.UnsupportedVersion, $"Protocol major version {startup.Version.Major} is not supported; the server speaks {ProtocolVersion.Current}.").ConfigureAwait(false);
            return false;
        }

        ProtocolVersion = negotiated;

        if (_engine.State != EngineState.Running)
        {
            await TryWriteErrorAsync(ProtocolErrorCode.Unavailable, $"The Blob engine is {_engine.State}.").ConfigureAwait(false);
            return false;
        }

        IDatabase? database = await ResolveDatabaseAsync(startup.Database, handshakeSource.Token).ConfigureAwait(false);

        if (database is null)
        {
            await TryWriteErrorAsync(ProtocolErrorCode.DatabaseNotFound, $"The server's engine has no database named '{startup.Database}'.").ConfigureAwait(false);
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
                    return;

                default:
                    await TryWriteErrorAsync(ProtocolErrorCode.ProtocolViolation, $"Unexpected {frame.Value.Type} frame in the ready state.").ConfigureAwait(false);
                    return;
            }
        }
    }

    private async Task<bool> ExecuteAsync(ProtocolFrame frame, CancellationToken cancellationToken)
    {
        if (_engine.State != EngineState.Running)
        {
            await WriteErrorAsync(ProtocolErrorCode.Unavailable, $"The Blob engine is {_engine.State}.", cancellationToken).ConfigureAwait(false);
            return false;
        }
        try
        {
            var database = (IBlobDatabase)_databaseSession!.Database;
            switch ((BlobProtocolMessageType)frame.Type)
            {
                case BlobProtocolMessageType.Write:
                    await UploadAsync(database, BlobWriteMessage.Decode(frame.Payload.Span), cancellationToken).ConfigureAwait(false);
                    break;
                case BlobProtocolMessageType.Read:
                    await DownloadAsync(database, BlobReadMessage.Decode(frame.Payload.Span), cancellationToken).ConfigureAwait(false);
                    break;
                case BlobProtocolMessageType.Delete:
                {
                    BlobDeleteMessage request = BlobDeleteMessage.Decode(frame.Payload.Span);
                    IBlobContainer container = await database.GetContainerAsync(request.Container, cancellationToken).ConfigureAwait(false);
                    bool deleted = await container.DeleteAsync(request.Name, cancellationToken).ConfigureAwait(false);
                    await CompleteAsync(deleted ? 1 : 0, cancellationToken).ConfigureAwait(false);
                    break;
                }
                case BlobProtocolMessageType.GetProperties:
                {
                    BlobGetPropertiesMessage request = BlobGetPropertiesMessage.Decode(frame.Payload.Span);
                    IBlobContainer container = await database.GetContainerAsync(request.Container, cancellationToken).ConfigureAwait(false);
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
                    IBlobContainer container = await database.GetContainerAsync(request.Container, cancellationToken).ConfigureAwait(false);
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
            // boundary or partially consumed content is returned to the connection pool.
            await TryWriteErrorAsync(ProtocolErrorCode.ExecutionFailure, exception.Message).ConfigureAwait(false);
            return false;
        }
    }

    private async Task UploadAsync(IBlobDatabase database, BlobWriteMessage request, CancellationToken cancellationToken)
    {
        ProtocolFrame? start = await _reader!.ReadFrameAsync(cancellationToken).ConfigureAwait(false);
        if (start is null || start.Value.Type != (ProtocolMessageType)BlobProtocolMessageType.TransferStart)
        {
            throw new ProtocolException("A Blob upload must begin with TransferStart.");
        }
        BlobTransferStartMessage metadata = BlobTransferStartMessage.Decode(start.Value.Payload.Span);
        await using IDatabaseTransaction transaction = await _databaseSession!.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        Stream? destination = null;
        try
        {
            IBlobContainer container = await database.GetContainerAsync(request.Container, cancellationToken).ConfigureAwait(false);
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
            if (transaction.State == TransactionState.Active)
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

    private async Task DownloadAsync(IBlobDatabase database, BlobReadMessage request, CancellationToken cancellationToken)
    {
        // Metadata and content must refer to the same immutable version even when replaced
        // concurrently. The read-only transaction keeps its snapshot until stream disposal.
        await using IDatabaseTransaction transaction = await _databaseSession!.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        IBlobContainer container = await database.GetContainerAsync(request.Container, cancellationToken).ConfigureAwait(false);
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
    private async ValueTask<IDatabase?> ResolveDatabaseAsync(string name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (_engine.TryGetDatabase(name, out IDatabase database))
        {
            return database;
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
