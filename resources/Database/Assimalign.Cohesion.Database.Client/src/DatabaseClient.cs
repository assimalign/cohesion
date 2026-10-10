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
    private readonly IConnectionFactory _connectionFactory;
    private readonly ProtocolMessageFamily _family;
    private readonly ConcurrentStack<DatabaseConnection> _idle = new();
    private readonly SemaphoreSlim _slots;
    private bool _isDisposed;

    private DatabaseClient(DatabaseConnectionSettings settings, IConnectionFactory connectionFactory, ProtocolMessageFamily family)
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
    /// <returns>An open connection; dispose it to return it to the pool.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the client, or an object its connection factory needs, is disposed.</exception>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> cancels the wait for a slot, the dial or the handshake; a dial failure the transport reports after the cancellation is its inner exception.</exception>
    /// <exception cref="DatabaseClientException">Thrown when the dial fails, with <see cref="ProtocolErrorCode.ConnectionFailure"/>, the endpoint in the message and the transport's exception (for example a <see cref="System.Net.Sockets.SocketException"/>, a TLS handshake failure or a connect timeout) as the inner exception; when the transport breaks during the handshake (the peer resets or closes the connection), with <see cref="ProtocolErrorCode.Internal"/> and the transport's exception, if any, as the inner exception; or when the server rejects the handshake, with the server's wire code, or breaks the protocol, with <see cref="ProtocolErrorCode.ProtocolViolation"/>.</exception>
    public async ValueTask<DatabaseConnection> RentAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        long waitStartTimestamp = DatabaseClientEventSource.Log.GetPoolTimestamp();

        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);

        long waitEndTimestamp = waitStartTimestamp == 0 ? 0 : Stopwatch.GetTimestamp();

        try
        {
            // Prefer an idle, still-healthy connection — this is the session
            // reuse the pool exists for.
            while (_idle.TryPop(out DatabaseConnection? idle))
            {
                if (idle.IsOpen)
                {
                    idle.MarkRented();
                    DatabaseClientEventSource.Log.ConnectionRented(idle, reused: true, waitStartTimestamp, waitEndTimestamp);
                    return idle;
                }

                await idle.CloseAsync().ConfigureAwait(false);
            }

            var connection = new DatabaseConnection(this, _connectionFactory, Settings, _family);

            try
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await connection.CloseAsync().ConfigureAwait(false);
                throw;
            }

            connection.MarkRented();
            DatabaseClientEventSource.Log.ConnectionRented(connection, reused: false, waitStartTimestamp, waitEndTimestamp);
            return connection;
        }
        catch
        {
            _slots.Release();
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
    internal async ValueTask ReturnAsync(DatabaseConnection connection)
    {
        try
        {
            if (!_isDisposed && connection.IsOpen)
            {
                // Written before the push, so a rental of the pooled connection is never traced first.
                DatabaseClientEventSource.Log.ConnectionReturned(connection, pooled: true);
                _idle.Push(connection);
            }
            else
            {
                DatabaseClientEventSource.Log.ConnectionReturned(connection, pooled: false);
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                _slots.Release();
            }
            catch (ObjectDisposedException)
            {
                // The client was disposed while this connection was rented; nothing
                // waits on the slot anymore.
            }
        }
    }

    /// <summary>
    /// Closes every idle pooled connection. Outstanding rentals close when they are returned.
    /// Idempotent.
    /// </summary>
    /// <returns>A task that completes when the idle connections are closed.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        while (_idle.TryPop(out DatabaseConnection? idle))
        {
            await idle.CloseAsync().ConfigureAwait(false);
        }

        _slots.Dispose();
    }
}
