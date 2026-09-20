using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Client;

namespace Assimalign.Cohesion.Database.Blob.Client;

internal sealed class DefaultBlobClient(IDatabaseClient client) : IBlobClient
{
    public DatabaseConnectionSettings Settings => client.Settings;

    public async ValueTask<IBlobConnection> ConnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return new BlobConnection(await client.RentAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (DatabaseClientException exception)
        {
            throw new BlobClientException(exception.Code, exception.Message, exception);
        }
    }

    public ValueTask DisposeAsync() => client.DisposeAsync();
}
