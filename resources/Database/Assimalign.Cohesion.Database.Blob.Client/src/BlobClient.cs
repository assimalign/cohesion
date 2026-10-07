using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Client;

namespace Assimalign.Cohesion.Database.Blob.Client;

/// <summary>Owns a pool of authenticated connections to one Blob database.</summary>
/// <remarks>
/// It layers the Blob transfer surface over the shared <see cref="DatabaseClient"/> core, which owns
/// pooling, framing and the wire handshake. Dispose rented connections before disposing the client.
/// </remarks>
public sealed class BlobClient : IAsyncDisposable
{
    private readonly DatabaseClient _client;

    private BlobClient(DatabaseClient client)
    {
        _client = client;
    }

    /// <summary>Creates a client without opening any connections.</summary>
    /// <param name="options">The connection settings and transport factory.</param>
    /// <returns>A client whose connections bind to the configured database during handshake.</returns>
    /// <exception cref="ArgumentNullException">The options are null.</exception>
    /// <exception cref="ArgumentException">The settings or factory are absent or invalid.</exception>
    public static BlobClient Create(BlobClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Settings is null || options.ConnectionFactory is null)
        {
            throw new ArgumentException("Connection settings and a connection factory are required.", nameof(options));
        }

        return new BlobClient(DatabaseClient.Create(new DatabaseClientOptions
        {
            Settings = options.Settings,
            ConnectionFactory = options.ConnectionFactory,
            Family = BlobProtocol.Family,
        }));
    }

    /// <summary>Gets the immutable connection settings.</summary>
    public DatabaseConnectionSettings Settings => _client.Settings;

    /// <summary>Rents an authenticated connection, waiting for a free pool slot when necessary.</summary>
    /// <param name="cancellationToken">Cancellation token for pool acquisition and handshake.</param>
    /// <returns>A connection that returns its lease when disposed.</returns>
    /// <exception cref="BlobClientException">The connection or handshake failed.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    /// <exception cref="ObjectDisposedException">The client is disposed.</exception>
    public async ValueTask<BlobConnection> ConnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return new BlobConnection(await _client.RentAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (DatabaseClientException exception)
        {
            throw new BlobClientException(exception.Code, exception.Message, exception);
        }
    }

    /// <summary>
    /// Closes every idle pooled connection; outstanding connections close when they are returned.
    /// </summary>
    /// <returns>A task that completes when the idle connections are closed.</returns>
    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
