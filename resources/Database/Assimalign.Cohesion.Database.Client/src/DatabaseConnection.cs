using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Database.Client.Internal;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Client;

/// <summary>
/// One authenticated client connection: a protocol session bound to a database on the server and
/// one immutable model message family.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DatabaseClient.RentAsync(CancellationToken)"/> returns a connection that has dialed its
/// transport and run the startup, authenticate and ready handshake. It runs model-owned framed
/// exchanges. Disposing it returns the rental to its pool with the authenticated server session intact
/// when it is still healthy.
/// </para>
/// <para>
/// Connections are not thread-safe: one exchange at a time, mirroring the engine-session contract on
/// the server side. An overlapping exchange is rejected before it writes any frame.
/// </para>
/// </remarks>
public sealed class DatabaseConnection : IAsyncDisposable
{
    private readonly DatabaseClient _owner;
    private readonly IConnectionFactory _connectionFactory;
    private readonly DatabaseConnectionSettings _settings;

    private IConnection? _connection;
    private ProtocolFrameReader? _reader;
    private ProtocolFrameWriter? _writer;
    private bool _isOpen;
    private bool _isRented;
    private bool _isClosed;
    private readonly object _exchangeLock = new();
    private CancellationTokenSource? _operation;
    private TaskCompletionSource? _exchangeCompletion;
    private Task? _returnTask;

    internal DatabaseConnection(DatabaseClient owner, IConnectionFactory connectionFactory, DatabaseConnectionSettings settings, ProtocolMessageFamily family)
    {
        _owner = owner;
        _connectionFactory = connectionFactory;
        _settings = settings;
        Family = family;
        Database = _settings.Database!;
        Principal = _settings.Principal;
    }

    /// <summary>
    /// Gets the database this connection is bound to.
    /// </summary>
    public string Database { get; }

    /// <summary>
    /// Gets the principal this connection authenticated as.
    /// </summary>
    public string Principal { get; }

    /// <summary>
    /// Gets the protocol version negotiated with the server.
    /// </summary>
    public ProtocolVersion ServerVersion { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the connection is open and usable.
    /// </summary>
    /// <remarks>
    /// An exchange failure that certified its complete response leaves the connection open; a
    /// protocol or transport failure, a cancellation, or an unfinished response marks it broken.
    /// </remarks>
    public bool IsOpen => _isOpen && _connection is { State: ConnectionState.Open or ConnectionState.Opening };

    /// <summary>Gets the message family fixed when the owning pool was created.</summary>
    public ProtocolMessageFamily Family { get; }

    internal void MarkRented()
    {
        lock (_exchangeLock)
        {
            _isRented = true;
            _returnTask = null;
        }
    }

    /// <summary>
    /// Opens the connection: dials the transport and runs the startup, authenticate and ready
    /// handshake. The owning client calls it once, before the first rental.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <exception cref="DatabaseClientException">Thrown when the server rejects the handshake (version, authentication, unknown database, capacity).</exception>
    internal async ValueTask OpenAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isClosed, this);

        if (_isOpen)
        {
            return;
        }

        _connection = await _connectionFactory.ConnectAsync(_settings.EndPoint!, cancellationToken).ConfigureAwait(false);

        Stream stream = _connection.AsStream();
        var channel = new ProtocolChannel(stream, Family, leaveOpen: true);
        _reader = channel.Reader;
        _writer = channel.Writer;

        var startup = new ProtocolStartupMessage(ProtocolVersion.Current, Database, Principal);
        await WriteFrameAsync(ProtocolMessageType.Startup, startup.Encode(), cancellationToken).ConfigureAwait(false);

        ProtocolFrame challenge = await ExpectFrameAsync(cancellationToken).ConfigureAwait(false);

        if (challenge.Type == ProtocolMessageType.Error)
        {
            // Startup rejections: unsupported version, unknown database, capacity.
            throw FromErrorFrame(challenge);
        }

        if (challenge.Type != ProtocolMessageType.Authenticate)
        {
            throw MarkBroken(new DatabaseClientException(ProtocolErrorCode.ProtocolViolation, $"Expected an authenticate challenge but received {challenge.Type}."));
        }

        // MVP trust method: the challenge carries no payload and the response
        // sends no evidence. Method-specific responses arrive with real
        // authenticator implementations.
        await WriteFrameAsync(ProtocolMessageType.AuthenticateResponse, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);

        ProtocolFrame ready = await ExpectFrameAsync(cancellationToken).ConfigureAwait(false);

        if (ready.Type == ProtocolMessageType.Error)
        {
            // Authentication rejection.
            throw FromErrorFrame(ready);
        }

        if (ready.Type != ProtocolMessageType.Ready)
        {
            throw MarkBroken(new DatabaseClientException(ProtocolErrorCode.ProtocolViolation, $"Expected a ready frame but received {ready.Type}."));
        }

        ServerVersion = ProtocolVersion.Current;
        _isOpen = true;
    }

    /// <summary>
    /// Executes one complete model-owned framed exchange.
    /// </summary>
    /// <typeparam name="TResult">The model's result type.</typeparam>
    /// <param name="exchange">The operation, bound to this connection's exact family instance.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The model-owned result.</returns>
    /// <exception cref="ArgumentNullException">The exchange is null.</exception>
    /// <exception cref="ArgumentException">The exchange belongs to a different family.</exception>
    /// <exception cref="InvalidOperationException">Another exchange is active.</exception>
    /// <exception cref="ObjectDisposedException">The rental has already been returned.</exception>
    /// <exception cref="DatabaseClientException">The server reports an error or the connection fails.</exception>
    /// <remarks>
    /// A non-virtual generic method on a sealed type, because NativeAOT never devirtualizes a generic
    /// virtual call.
    /// </remarks>
    public async ValueTask<TResult> ExecuteAsync<TResult>(DatabaseProtocolExchange<TResult> exchange, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        if (!ReferenceEquals(Family, exchange.Family))
        {
            throw new ArgumentException("The exchange belongs to a different message family.", nameof(exchange));
        }
        CancellationTokenSource operation;
        TaskCompletionSource completion;
        lock (_exchangeLock)
        {
            if (!IsOpen)
            {
                throw new DatabaseClientException(ProtocolErrorCode.Internal, "The connection is not open.");
            }
            ObjectDisposedException.ThrowIf(!_isRented, this);
            if (_operation is not null)
            {
                throw new InvalidOperationException("An exchange is already active on this connection.");
            }
            operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _operation = operation;
            _exchangeCompletion = completion;
        }

        try
        {
            return await exchange.ExecuteAsync(_reader!, _writer!, operation.Token).ConfigureAwait(false);
        }
        catch (DatabaseClientException)
        {
            // The exchange knows whether it consumed a terminal, reusable response.
            if (!exchange.IsResponseComplete)
            {
                _isOpen = false;
            }
            throw;
        }
        catch (ProtocolException exception)
        {
            throw MarkBroken(new DatabaseClientException(ProtocolErrorCode.ProtocolViolation, exception.Message, exception));
        }
        catch (Exception exception) when (exception is IOException or ConnectionAbortedException or ConnectionResetException)
        {
            throw MarkBroken(new DatabaseClientException(ProtocolErrorCode.Internal, "The connection failed during an exchange.", exception));
        }
        catch (OperationCanceledException)
        {
            // Cancellation or a failed decoder may leave an unfinished response.
            _isOpen = false;
            throw;
        }
        catch
        {
            if (!exchange.IsResponseComplete)
            {
                _isOpen = false;
            }
            throw;
        }
        finally
        {
            lock (_exchangeLock)
            {
                _operation = null;
                _exchangeCompletion = null;
                operation.Dispose();
                completion.TrySetResult();
            }
        }
    }

    /// <summary>Starts a bounded download while its framed exchange remains active.</summary>
    /// <param name="exchange">The model operation, bound to this connection's exact family instance.</param>
    /// <param name="cancellationToken">Cancels startup and the entire returned stream's lifetime.</param>
    /// <returns>A sequential readable stream after the model validates its opening response.</returns>
    /// <exception cref="ArgumentNullException">The exchange is null.</exception>
    /// <exception cref="ArgumentException">The exchange belongs to another family.</exception>
    /// <exception cref="InvalidOperationException">Another exchange is active.</exception>
    /// <exception cref="DatabaseClientException">The server rejects the operation or the connection fails.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
    /// <exception cref="ObjectDisposedException">The rental has already been returned.</exception>
    /// <remarks>
    /// Dispose the returned stream. Early disposal or cancellation aborts the exchange and returns its
    /// unusable rental. Verified completion retains this connection's lease for subsequent operations;
    /// dispose the connection to return it to the pool. Disposing the connection cancels and joins any
    /// active exchange. Later failures surface from stream reads and cannot appear as successful EOF.
    /// </remarks>
    public ValueTask<Stream> ExecuteStreamingAsync(DatabaseStreamingExchange exchange, CancellationToken cancellationToken = default)
        => DatabaseDownloadStream.CreateAsync(this, exchange, cancellationToken);

    /// <summary>Discards this rental and closes its session instead of returning it to the pool.</summary>
    /// <returns>The asynchronous transport teardown operation.</returns>
    /// <remarks>Use when application-level session state cannot be reset safely. Aborting cancels
    /// and joins an active exchange. It cannot undo a transaction already committed by the server
    /// or determine an unacknowledged command's outcome. This operation is deliberately not cancellable:
    /// an unsafe rental must not survive cleanup merely because its caller cancelled.</remarks>
    public ValueTask AbortAsync()
    {
        lock (_exchangeLock)
        {
            if (_isRented)
            {
                _isOpen = false;
            }
            return DisposeAsync();
        }
    }

    /// <summary>
    /// Returns the rental to its pool, after canceling and joining an active exchange. A healthy
    /// connection keeps its authenticated server session for the next rental; a broken one closes.
    /// Idempotent for the current rental.
    /// </summary>
    /// <returns>A task that completes when the rental is returned.</returns>
    public ValueTask DisposeAsync()
    {
        lock (_exchangeLock)
        {
            if (_returnTask is not null)
            {
                return new ValueTask(_returnTask);
            }
            if (!_isRented)
            {
                return ValueTask.CompletedTask;
            }
            _isRented = false;
            _operation?.Cancel();
            _returnTask = ReturnAfterExchangeAsync(_exchangeCompletion?.Task);
            return new ValueTask(_returnTask);
        }
    }

    private async Task ReturnAfterExchangeAsync(Task? completion)
    {
        if (completion is not null)
        {
            await completion.ConfigureAwait(false);
        }
        await _owner.ReturnAsync(this).ConfigureAwait(false);
    }

    /// <summary>
    /// Closes the wire connection for real: best-effort terminate frame, then
    /// transport teardown. Idempotent.
    /// </summary>
    internal async ValueTask CloseAsync()
    {
        if (_isClosed)
        {
            return;
        }

        _isClosed = true;

        if (_isOpen && _writer is not null)
        {
            _isOpen = false;

            try
            {
                await _writer.WriteFrameAsync(new ProtocolFrame(ProtocolMessageType.Terminate, ReadOnlyMemory<byte>.Empty)).ConfigureAwait(false);
                await _writer.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Best effort; the server may already be gone.
            }
        }

        _isOpen = false;

        if (_reader is not null)
        {
            await _reader.DisposeAsync().ConfigureAwait(false);
        }

        if (_writer is not null)
        {
            await _writer.DisposeAsync().ConfigureAwait(false);
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask WriteFrameAsync(ProtocolMessageType type, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        try
        {
            await _writer!.WriteFrameAsync(new ProtocolFrame(type, payload), cancellationToken).ConfigureAwait(false);
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ConnectionAbortedException or ConnectionResetException)
        {
            throw MarkBroken(new DatabaseClientException(ProtocolErrorCode.Internal, "The connection failed while sending a frame.", exception));
        }
    }

    /// <summary>
    /// Reads the next frame, translating transport failures and unexpected
    /// end-of-stream into client exceptions that mark the connection broken.
    /// </summary>
    private async ValueTask<ProtocolFrame> ExpectFrameAsync(CancellationToken cancellationToken)
    {
        ProtocolFrame? frame;

        try
        {
            frame = await _reader!.ReadFrameAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ProtocolException exception)
        {
            throw MarkBroken(new DatabaseClientException(ProtocolErrorCode.ProtocolViolation, exception.Message, exception));
        }
        catch (Exception exception) when (exception is IOException or ConnectionAbortedException or ConnectionResetException)
        {
            throw MarkBroken(new DatabaseClientException(ProtocolErrorCode.Internal, "The connection failed while awaiting a frame.", exception));
        }

        if (frame is null)
        {
            throw MarkBroken(new DatabaseClientException(ProtocolErrorCode.Internal, "The server closed the connection mid-exchange."));
        }

        return frame.Value;
    }

    private DatabaseClientException MarkBroken(DatabaseClientException exception)
    {
        _isOpen = false;
        return exception;
    }

    /// <summary>
    /// Translates a handshake error frame into a client exception carrying the
    /// server's wire code; the connection never reached ready, so it is broken.
    /// </summary>
    private DatabaseClientException FromErrorFrame(ProtocolFrame frame)
    {
        ProtocolErrorMessage error = ProtocolErrorMessage.Decode(frame.Payload.Span);
        return MarkBroken(new DatabaseClientException(error.Code, error.Message));
    }
}
