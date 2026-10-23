using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Database.Client.Internal;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Client;

/// <summary>
/// A pooling database client: rents authenticated connections to one database on one server,
/// reusing sessions across rents.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Create(DatabaseClientOptions)"/> performs no I/O. A slot semaphore bounds the total
/// connections at the settings' pool size, and an idle stack reuses authenticated sessions.
/// </para>
/// <para>
/// Renting returns an open, authenticated <see cref="DatabaseConnection"/>; disposing a rented
/// connection returns it to the pool (healthy connections keep their server session alive for the
/// next rent). Disposing the client closes every pooled connection; outstanding rentals close when
/// they are returned.
/// </para>
/// </remarks>
public sealed class DatabaseClient : IAsyncDisposable
{
    private readonly IDuplexConnectionFactory _connectionFactory;
    private readonly ProtocolMessageFamily _family;
    private readonly ConcurrentStack<DatabaseConnector> _idle = new();
    private readonly SemaphoreSlim _slots;
    // Serializes semaphore entry/release/disposal and idle-stack admission/removal. Never held
    // across frame I/O, cancellation callbacks or EventSource writes (listeners may dispose us).
    private readonly object _poolLock = new();
    private readonly CancellationTokenSource _disposeCancellation = new();
    private int _waitingRents;
    private TaskCompletionSource? _waitersCompletion;
    private int _isDisposed;

    private DatabaseClient(DatabaseConnectionSettings settings, IDuplexConnectionFactory connectionFactory, ProtocolMessageFamily family)
    {
        Settings = settings;
        _connectionFactory = connectionFactory;
        _family = family;
        _slots = new SemaphoreSlim(settings.MaxPoolSize, settings.MaxPoolSize);
    }

    /// <summary>
    /// Creates a pooling client from options. Connections dial lazily — creation
    /// performs no I/O.
    /// </summary>
    /// <param name="options">The composition options. Requires settings with a database and endpoint, a connection factory, and the model's message family.</param>
    /// <returns>The client.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when the options carry no settings, no connection factory, no family, no database name, no endpoint, or a non-positive pool size.</exception>
    public static DatabaseClient Create(DatabaseClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Settings is null)
        {
            throw new ArgumentException("Connection settings are required.", nameof(options));
        }
        if (options.ConnectionFactory is null)
        {
            throw new ArgumentException("A connection factory is required.", nameof(options));
        }
        if (string.IsNullOrWhiteSpace(options.Settings.Database))
        {
            throw new ArgumentException("The settings must name a database.", nameof(options));
        }
        if (options.Settings.EndPoint is null)
        {
            throw new ArgumentException("The settings must carry an endpoint (an Endpoint=host[:port] connection string key, or a typed EndPoint).", nameof(options));
        }
        if (options.Settings.MaxPoolSize <= 0)
        {
            throw new ArgumentException("The pool size must be positive.", nameof(options));
        }

        if (options.Family is null)
        {
            throw new ArgumentException("A model message family is required.", nameof(options));
        }

        return new DatabaseClient(options.Settings, options.ConnectionFactory, options.Family);
    }

    /// <summary>
    /// Gets the connection settings the client was composed with.
    /// </summary>
    public DatabaseConnectionSettings Settings { get; }

    /// <summary>
    /// Rents an open, authenticated connection from the pool, dialing and
    /// handshaking a new one when no pooled connection is idle and the pool is
    /// under its size limit. Waits when the pool is exhausted.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation, including the wait for a free slot.</param>
    /// <returns>A new lease over an open authenticated session; dispose it to return the session to the pool. The lease remains disposed even if the session is rented again.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the client, or an object its connection factory needs, is disposed.</exception>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> cancels the wait for a slot, the dial or the handshake; a dial failure the transport reports after the cancellation is its inner exception.</exception>
    /// <exception cref="DatabaseClientException">Thrown when the dial fails, with <see cref="ProtocolErrorCode.ConnectionFailure"/>, the endpoint in the message and the transport's exception (for example a <see cref="System.Net.Sockets.SocketException"/>, a TLS handshake failure or a connect timeout) as the inner exception; when the transport breaks during the handshake (the peer resets or closes the connection), with <see cref="ProtocolErrorCode.Internal"/> and the transport's exception, if any, as the inner exception; or when the server rejects the handshake, with the server's wire code, or breaks the protocol, with <see cref="ProtocolErrorCode.ProtocolViolation"/>.</exception>
    public async ValueTask<DatabaseConnection> RentAsync(CancellationToken cancellationToken = default)
    {
        long waitStartTimestamp = DatabaseClientEventSource.Log.GetPoolTimestamp();

        CancellationTokenSource waiting;
        Task slot;
        lock (_poolLock)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);
            waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCancellation.Token);
            slot = _slots.WaitAsync(waiting.Token);
            _waitingRents++;
        }
        try
        {
            await slot.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (Volatile.Read(ref _isDisposed) != 0)
        {
            throw new ObjectDisposedException(nameof(DatabaseClient));
        }
        finally
        {
            waiting.Dispose();
            lock (_poolLock)
            {
                if (--_waitingRents == 0)
                {
                    _waitersCompletion?.TrySetResult();
                }
            }
        }

        long waitEndTimestamp = waitStartTimestamp == 0 ? 0 : Stopwatch.GetTimestamp();

        try
        {
            // Prefer an idle, still-healthy connection — this is the session
            // reuse the pool exists for.
            while (true)
            {
                DatabaseConnector? idle;
                lock (_poolLock)
                {
                    ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);
                    if (!_idle.TryPop(out idle))
                    {
                        break;
                    }
                }
                if (idle.IsOpen)
                {
                    var rental = new DatabaseConnection(this, idle);
                    DatabaseClientEventSource.Log.ConnectionRented(idle, reused: true, waitStartTimestamp, waitEndTimestamp);
                    return rental;
                }

                await idle.CloseAsync().ConfigureAwait(false);
            }

            var connection = new DatabaseConnector(_connectionFactory, Settings, _family);

            try
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                // A dial admitted before shutdown may finish after the idle pool was drained.
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);
            }
            catch
            {
                await connection.CloseAsync().ConfigureAwait(false);
                throw;
            }

            var lease = new DatabaseConnection(this, connection);
            DatabaseClientEventSource.Log.ConnectionRented(connection, reused: false, waitStartTimestamp, waitEndTimestamp);
            return lease;
        }
        catch
        {
            ReleaseSlot();
            throw;
        }
    }

    /// <summary>Rents a connection and starts a bounded, nonseekable response stream.</summary>
    /// <param name="exchange">The model operation, bound to the client's exact message family.</param>
    /// <param name="cancellationToken">Cancellation for the rental and the returned stream's lifetime.</param>
    /// <returns>A readable stream after the operation's initial metadata has been validated.</returns>
    /// <remarks>
    /// The caller must dispose the returned stream. Its lease remains reserved through successful
    /// EOF until disposal. Failure, cancellation, or disposal before verified exchange completion
    /// closes the connection and releases its lease. Both synchronous and asynchronous reads are
    /// supported; canceling a read token cancels and joins the entire exchange. A failed response
    /// never becomes successful EOF, and subsequent reads retain its original failure. An exchange of
    /// another family is rejected before a connection is rented.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The exchange is null.</exception>
    /// <exception cref="ArgumentException">The exchange belongs to a different message family.</exception>
    /// <exception cref="ObjectDisposedException">The client is disposed.</exception>
    /// <exception cref="DatabaseClientException">The server rejects the operation or the connection fails.</exception>
    /// <exception cref="OperationCanceledException">The rental or streaming operation is canceled.</exception>
    public async ValueTask<Stream> ExecuteStreamingAsync(DatabaseStreamingExchange exchange,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        if (!ReferenceEquals(_family, exchange.Family))
        {
            throw new ArgumentException("The exchange belongs to a different message family.", nameof(exchange));
        }
        DatabaseConnection connection = await RentAsync(cancellationToken).ConfigureAwait(false);
        return await DatabaseDownloadStream.CreateAsync(connection, exchange, cancellationToken,
            ownsConnection: true).ConfigureAwait(false);
    }

    /// <summary>
    /// Accepts a connection back from a rent: healthy connections go to the idle
    /// stack with their session intact; broken ones close. Always frees the slot.
    /// </summary>
    internal async ValueTask ReturnAsync(DatabaseConnector connection)
    {
        try
        {
            if (Volatile.Read(ref _isDisposed) == 0 && connection.IsOpen &&
                await connection.ResetAsync().ConfigureAwait(false))
            {
                // Written before the push, so a rental of the pooled connection is never traced first.
                DatabaseClientEventSource.Log.ConnectionReturned(connection, pooled: true);
                lock (_poolLock)
                {
                    _idle.Push(connection);
                    if (Volatile.Read(ref _isDisposed) == 0)
                    {
                        return;
                    }
                    // Shutdown can win after the initial check, including from the synchronous
                    // return event. Rent and shutdown cannot pop this connector under this lock.
                    _idle.TryPop(out _);
                }
            }
            else
            {
                DatabaseClientEventSource.Log.ConnectionReturned(connection, pooled: false);
            }
            await connection.CloseAsync().ConfigureAwait(false);
        }
        finally
        {
            ReleaseSlot();
        }
    }

    private void ReleaseSlot()
    {
        lock (_poolLock)
        {
            if (Volatile.Read(ref _isDisposed) == 0)
            {
                _slots.Release();
            }
        }
    }

    /// <summary>
    /// Closes every idle pooled connection. Outstanding rentals close when they are returned.
    /// Pending rents end with <see cref="ObjectDisposedException"/>. Idempotent.
    /// </summary>
    /// <returns>A task that completes when the idle connections are closed.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
        {
            return;
        }

        // Only semaphore waits link this token; no model or transport callback runs here.
        // Cancel before disposing the semaphore, which does not itself complete pending waits.
        _disposeCancellation.Cancel();
        Task waiters;
        lock (_poolLock)
        {
            waiters = _waitingRents == 0 ? Task.CompletedTask :
                (_waitersCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
        // Semaphore cancellation unlinks waiters asynchronously. Disposing it before they finish
        // can erase its waiter list and strand a second waiter even though its token was canceled.
        await waiters.ConfigureAwait(false);
        while (true)
        {
            DatabaseConnector? idle;
            lock (_poolLock)
            {
                if (!_idle.TryPop(out idle))
                {
                    _slots.Dispose();
                    _disposeCancellation.Dispose();
                    break;
                }
            }
            await idle.CloseAsync().ConfigureAwait(false);
        }
    }
}
