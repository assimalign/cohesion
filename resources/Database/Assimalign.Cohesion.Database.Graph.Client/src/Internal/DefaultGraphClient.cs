using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Client;

namespace Assimalign.Cohesion.Database.Graph.Client.Internal;

internal sealed class DefaultGraphClient : IGraphClient
{
    private readonly IDatabaseClient _client;

    /// <summary>
    /// Initializes a new instance of the <see cref="DefaultGraphClient"/> class.
    /// </summary>
    /// <param name="client">The database client that rents pooled connections.</param>
    public DefaultGraphClient(IDatabaseClient client)
    {
        _client = client;
    }

    public DatabaseConnectionSettings Settings => _client.Settings;

    public async ValueTask<IGraphConnection> ConnectAsync(CancellationToken cancellationToken = default)
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

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}

