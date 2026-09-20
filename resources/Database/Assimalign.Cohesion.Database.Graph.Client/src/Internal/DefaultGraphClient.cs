using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Client;

namespace Assimalign.Cohesion.Database.Graph.Client;

internal sealed class DefaultGraphClient(IDatabaseClient client) : IGraphClient
{
    public DatabaseConnectionSettings Settings => client.Settings;

    public async ValueTask<IGraphConnection> ConnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return new GraphConnection(await client.RentAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (DatabaseClientException exception)
        {
            throw new GraphClientException(exception.Code, exception.Message, exception);
        }
    }

    public ValueTask DisposeAsync() => client.DisposeAsync();
}

