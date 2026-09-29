using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Client;

namespace Assimalign.Cohesion.Database.Blob.Client.Internal;

internal sealed class DefaultBlobClient : IBlobClient
{
    private readonly IDatabaseClient _client;

    /// <summary>
    /// Initializes a new instance of the <see cref="DefaultBlobClient"/> class.
    /// </summary>
    /// <param name="client">The shared database client that rents the connections behind each Blob connection.</param>
    public DefaultBlobClient(IDatabaseClient client)
    {
        _client = client;
    }

    public DatabaseConnectionSettings Settings => _client.Settings;

    public async ValueTask<IBlobConnection> ConnectAsync(CancellationToken cancellationToken = default)
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

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
