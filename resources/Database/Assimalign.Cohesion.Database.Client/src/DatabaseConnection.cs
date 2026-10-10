using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
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
/// <para>
/// The pool hands out this same instance on every rental of its session, so a reference kept after
/// <see cref="DisposeAsync"/> is not a lease: once the pool rents the instance again, calls through the
/// stale reference act on the new caller's rental, and a second dispose returns it. Drop the reference
/// when you dispose it. The model clients' connections wrap the rental and refuse every call after
/// their own disposal.
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
    private int _opened;
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
            if (!_isRented)
            {
                DatabaseClientEventSource.Log.RentalStarted();
            }
            _isRented = true;
            _returnTask = null;
        }
    }

    /// <summary>
    /// Opens the connection: dials the transport and runs the startup, authenticate and ready
    /// handshake. The owning client calls it once, before the first rental.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <exception cref="DatabaseClientException">Thrown when the dial fails (<see cref="ProtocolErrorCode.ConnectionFailure"/>, with the factory's exception as the inner exception), the transport breaks during the handshake (<see cref="ProtocolErrorCode.Internal"/>, with the transport's exception as the inner exception), the server breaks the protocol (<see cref="ProtocolErrorCode.ProtocolViolation"/>) or the server rejects the handshake (version, authentication, unknown database, capacity).</exception>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> cancels the dial or the handshake; a dial failure the transport reports after the cancellation is its inner exception.</exception>
    /// <exception cref="ObjectDisposedException">Thrown when the connection, or an object the connection factory needs, is disposed.</exception>
    internal async ValueTask OpenAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isClosed, this);

        if (_isOpen)
        {
            return;
        }

        long startTimestamp = DatabaseClientEventSource.Log.GetTimestamp();

        // A failed open is captured by the filter, which declines it, and written from the finally,
        // once what the open threw from has unwound.
        DatabaseClientException? openFailure = null;
        try
        {
            _connection = await DialAsync(cancellationToken).ConfigureAwait(false);

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
        }
        catch (DatabaseClientException exception) when (CaptureOpenFailure(exception, out openFailure))
        {
            // Unreachable: the filter records the failure and declines it, so it propagates
            // unchanged.
            throw;
        }
        finally
        {
            if (openFailure is not null)
            {
                DatabaseClientEventSource.Log.ConnectionOpenFailed(this, openFailure, startTimestamp);
            }
        }

        ServerVersion = ProtocolVersion.Current;
        _isOpen = true;

        // The flag the first close exchanges: a connection that opened is counted closed once.
        Volatile.Write(ref _opened, 1);
        DatabaseClientEventSource.Log.ConnectionOpened(this, startTimestamp);
    }

    /// <summary>
    /// Records a failed open and declines it, so the exception filter that calls it never catches;
    /// the open writes the failure from its finally.
    /// </summary>
    /// <returns>Always false.</returns>
    private static bool CaptureOpenFailure(DatabaseClientException exception, out DatabaseClientException? captured)
    {
        captured = exception;
        return false;
    }

    /// <summary>
    /// Dials the transport. A failure of the connection factory (a refused or unreachable
    /// endpoint, a TLS handshake failure, a connect timeout) becomes a
    /// <see cref="DatabaseClientException"/> with <see cref="ProtocolErrorCode.ConnectionFailure"/>,
    /// the endpoint in its message and the factory's exception as its inner exception, the way
    /// Npgsql's connector wraps a failed connect in <c>NpgsqlException</c>.
    /// </summary>
    /// <remarks>
    /// The caller's cancellation and a disposed object propagate unchanged. A failure the
    /// transport reports after the caller canceled (a socket aborted by the cancellation, say)
    /// is the caller's cancellation too: it becomes an <see cref="OperationCanceledException"/>
    /// for the caller's token, with the transport's exception inside, as Npgsql checks the
    /// caller's token before it classifies a failed connect. A cancellation the caller did not
    /// request is the transport's own timeout (the TLS layer cancels a handshake that outlives
    /// its timeout), so it is wrapped as a dial failure too, as Npgsql turns such a cancellation
    /// into a timeout.
    /// </remarks>
    private async ValueTask<IConnection> DialAsync(CancellationToken cancellationToken)
    {
        EndPoint endPoint = _settings.EndPoint!;

        try
        {
            return await _connectionFactory.ConnectAsync(endPoint, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DatabaseClientException(ProtocolErrorCode.ConnectionFailure, $"Failed to connect to {Describe(endPoint)}: the connection attempt timed out.", exception);
        }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested && exception is not (OperationCanceledException or ObjectDisposedException or OutOfMemoryException))
        {
            throw new OperationCanceledException("The connection attempt was canceled.", exception, cancellationToken);
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or ObjectDisposedException or OutOfMemoryException))
        {
            // Broad on purpose: IConnectionFactory is an open seam and each transport throws its
            // own types (SocketException, IOException, AuthenticationException, TimeoutException,
            // ConnectionAbortedException), so a list would let the next transport's failure escape.
            throw new DatabaseClientException(ProtocolErrorCode.ConnectionFailure, $"Failed to connect to {Describe(endPoint)}: {exception.Message}", exception);
        }
    }

    /// <summary>
    /// Names an endpoint the way a reader writes it: <c>host:port</c> for a
    /// <see cref="DnsEndPoint"/> (whose own text adds the address family, as in
    /// <c>Unspecified/host:port</c>), with an IPv6 literal in brackets, and the endpoint's own
    /// text otherwise.
    /// </summary>
    private static string Describe(EndPoint endPoint)
        => endPoint switch
        {
            DnsEndPoint dns when dns.Host.Contains(':') && !dns.Host.StartsWith('[') => $"[{dns.Host}]:{dns.Port}",
            DnsEndPoint dns => $"{dns.Host}:{dns.Port}",
            _ => endPoint.ToString() ?? endPoint.GetType().Name,
        };

    /// <summary>
    /// Names the endpoint the connection dials, for the event source.
    /// </summary>
    internal string DescribeEndPoint() => Describe(_settings.EndPoint!);

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
    /// <exception cref="ObjectDisposedException">The rental has been returned and the pool has not rented this instance again.</exception>
    /// <exception cref="OperationCanceledException">The exchange is canceled, which marks the connection broken.</exception>
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
        catch (DatabaseClientException exception) when (exception.Code == ProtocolErrorCode.ConnectionFailure)
        {
            // Only the dial raises the client-local code, so an exchange that reports it decoded it
            // from a server's error frame: a peer that is not a conforming server. The decoded
            // exception is not kept as the inner exception, so no exception chain carries the code
            // for anything but a failed dial.
            throw MarkBroken(new DatabaseClientException(ProtocolErrorCode.ProtocolViolation, $"The server sent the client-local {nameof(ProtocolErrorCode.ConnectionFailure)} code: {exception.Message}"));
        }
        catch (DatabaseClientException exception)
        {
            DatabaseClientEventSource.Log.ExchangeFailed(this, exception);

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
        catch (Exception exception) when (IsTransportFailure(exception))
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
    /// <exception cref="ObjectDisposedException">The rental has been returned and the pool has not rented this instance again.</exception>
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
            DatabaseClientEventSource.Log.RentalEnded();
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

        // _isClosed is checked and set without synchronization, so the gauge counts the close of
        // an opened connection behind its own exchange.
        if (Interlocked.Exchange(ref _opened, 0) == 1)
        {
            DatabaseClientEventSource.Log.ConnectionClosed(this);
        }

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
        catch (Exception exception) when (IsTransportFailure(exception))
        {
            throw MarkBroken(new DatabaseClientException(ProtocolErrorCode.Internal, "The connection failed while sending a frame.", exception));
        }
    }

    /// <summary>
    /// Tells whether an exception is the transport failing under an open connection. A
    /// <see cref="SocketException"/> is one of them: the TCP transport completes the connection's
    /// input with the raw socket error when the peer resets the connection, and a
    /// <see cref="SocketException"/> is not an <see cref="IOException"/>.
    /// </summary>
    private static bool IsTransportFailure(Exception exception)
        => exception is IOException or SocketException or ConnectionAbortedException or ConnectionResetException;

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
        catch (Exception exception) when (IsTransportFailure(exception))
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
        // An open that fails never opened, so its failure is ConnectionOpenFailed, not a break.
        if (_isOpen)
        {
            DatabaseClientEventSource.Log.ConnectionBroken(this, exception);
        }

        _isOpen = false;
        return exception;
    }

    /// <summary>
    /// Translates a handshake error frame into a client exception carrying the
    /// server's wire code; the connection never reached ready, so it is broken.
    /// </summary>
    /// <remarks>
    /// A malformed error payload, or one that carries the client-local
    /// <see cref="ProtocolErrorCode.ConnectionFailure"/> code no conforming server sends, is a
    /// <see cref="ProtocolErrorCode.ProtocolViolation"/>, so the code keeps meaning a failed dial.
    /// </remarks>
    private DatabaseClientException FromErrorFrame(ProtocolFrame frame)
    {
        ProtocolErrorMessage error;

        try
        {
            error = ProtocolErrorMessage.Decode(frame.Payload.Span);
        }
        catch (ProtocolException exception)
        {
            return MarkBroken(new DatabaseClientException(ProtocolErrorCode.ProtocolViolation, exception.Message, exception));
        }

        if (error.Code == ProtocolErrorCode.ConnectionFailure)
        {
            return MarkBroken(new DatabaseClientException(ProtocolErrorCode.ProtocolViolation, $"The server sent the client-local {nameof(ProtocolErrorCode.ConnectionFailure)} code: {error.Message}"));
        }

        return MarkBroken(new DatabaseClientException(error.Code, error.Message));
    }
}
