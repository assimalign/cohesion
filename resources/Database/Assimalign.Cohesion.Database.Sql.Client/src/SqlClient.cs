using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Client;

namespace Assimalign.Cohesion.Database.Sql.Client;

/// <summary>
/// A pooling SQL client: hands out typed connections to one SQL database on one server, reusing
/// authenticated sessions across connects.
/// </summary>
/// <remarks>
/// The typed SQL surface layers over the shared <see cref="DatabaseClient"/> core: it adds commands,
/// typed result sets, a SQL-scoped error surface, and a telemetry hook, and delegates pooling,
/// framing, and the wire handshake to the core. Disposing the client closes every pooled
/// connection.
/// </remarks>
public sealed class SqlClient : IAsyncDisposable
{
    private readonly DatabaseClient _client;
    private readonly SqlClientObserver? _observer;

    private SqlClient(DatabaseClient client, SqlClientObserver? observer)
    {
        _client = client;
        _observer = observer;
    }

    /// <summary>
    /// Creates a pooling SQL client from options. Connections dial lazily — creation
    /// performs no I/O.
    /// </summary>
    /// <param name="options">The composition options. Requires settings with a database and endpoint, and a connection factory.</param>
    /// <returns>The client.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when the options carry no settings or no connection factory, or the settings are invalid.</exception>
    public static SqlClient Create(SqlClientOptions options)
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

        // The shared core validates the settings and factory in full (database,
        // endpoint, pool size); let its ArgumentException surface with its message.
        DatabaseClient client = DatabaseClient.Create(new DatabaseClientOptions
        {
            Settings = options.Settings,
            ConnectionFactory = options.ConnectionFactory,
            Family = SqlProtocol.Family,
        });

        return new SqlClient(client, options.Observer);
    }

    /// <summary>
    /// Gets the connection settings the client was composed with.
    /// </summary>
    public DatabaseConnectionSettings Settings => _client.Settings;

    /// <summary>
    /// Rents an open, authenticated typed connection from the pool, dialing and
    /// handshaking a new one when none is idle and the pool is under its limit.
    /// Waits when the pool is exhausted.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation, including the wait for a free slot.</param>
    /// <returns>An open typed connection; dispose it to return it to the pool.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the client is disposed.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the wait for a slot, the dial or the handshake is canceled.</exception>
    /// <exception cref="SqlClientException">Thrown when the handshake fails; a transport dial failure propagates from the connection factory.</exception>
    public async ValueTask<SqlConnection> ConnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            DatabaseConnection connection = await _client.RentAsync(cancellationToken).ConfigureAwait(false);
            return new SqlConnection(connection, _observer);
        }
        catch (DatabaseClientException exception)
        {
            throw SqlClientException.FromClientException(exception);
        }
    }

    /// <summary>
    /// Closes every idle pooled connection; outstanding connections close when they are returned.
    /// </summary>
    /// <returns>A task that completes when the idle connections are closed.</returns>
    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
