using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Client;

namespace Assimalign.Cohesion.Database.Graph.Client;

/// <summary>A pooled client for graph statements and path queries.</summary>
/// <remarks>
/// It layers the graph surface over the shared <see cref="DatabaseClient"/> core, which owns pooling,
/// framing and the wire handshake. Disposing the client closes every pooled connection.
/// </remarks>
public sealed class GraphClient : IAsyncDisposable
{
    private readonly DatabaseClient _client;

    private GraphClient(DatabaseClient client)
    {
        _client = client;
    }

    /// <summary>Creates a graph client that opens connections on demand.</summary>
    /// <param name="options">The database settings and transport factory.</param>
    /// <returns>A pooling graph client.</returns>
    /// <exception cref="ArgumentNullException">The options are null.</exception>
    /// <exception cref="ArgumentException">The settings or factory are absent or invalid.</exception>
    public static GraphClient Create(GraphClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Settings is null || options.ConnectionFactory is null)
        {
            throw new ArgumentException("Connection settings and a connection factory are required.", nameof(options));
        }
        return new GraphClient(DatabaseClient.Create(new DatabaseClientOptions
        {
            Settings = options.Settings,
            ConnectionFactory = options.ConnectionFactory,
            Family = GraphProtocol.Family,
        }));
    }

    /// <summary>Gets the endpoint, authentication, database, and pool settings.</summary>
    public DatabaseConnectionSettings Settings => _client.Settings;

    /// <summary>Rents an authenticated connection bound to the configured database.</summary>
    /// <param name="cancellationToken">Cancellation token for connecting.</param>
    /// <returns>The rented connection; dispose it to return its session.</returns>
    /// <exception cref="ObjectDisposedException">The client is disposed.</exception>
    /// <exception cref="OperationCanceledException">The wait for a slot, the dial or the handshake is canceled.</exception>
    /// <exception cref="GraphClientException">The handshake failed; a transport dial failure propagates from the connection factory.</exception>
    public async ValueTask<GraphConnection> ConnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return new GraphConnection(await _client.RentAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (DatabaseClientException exception)
        {
            throw new GraphClientException(exception.Code, exception.Message, exception);
        }
    }

    /// <summary>
    /// Closes every idle pooled connection; outstanding connections close when they are returned.
    /// </summary>
    /// <returns>A task that completes when the idle connections are closed.</returns>
    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
