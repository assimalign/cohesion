using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Client;

namespace Assimalign.Cohesion.Database.Sql.Client;

/// <summary>
/// The default SQL client: a thin typed facade over the shared pooling client core.
/// It delegates dialing, the handshake, and pooling to the core and wraps each
/// rented connection in a typed <see cref="SqlConnection"/>.
/// </summary>
internal sealed class DefaultSqlClient : ISqlClient
{
    private readonly IDatabaseClient _client;
    private readonly ISqlClientObserver? _observer;

    internal DefaultSqlClient(IDatabaseClient client, DatabaseConnectionSettings settings, ISqlClientObserver? observer)
    {
        _client = client;
        Settings = settings;
        _observer = observer;
    }

    /// <inheritdoc />
    public DatabaseConnectionSettings Settings { get; }

    /// <inheritdoc />
    public async ValueTask<ISqlConnection> ConnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            IDatabaseConnection connection = await _client.RentAsync(cancellationToken).ConfigureAwait(false);
            return new SqlConnection(connection, _observer);
        }
        catch (DatabaseClientException exception)
        {
            throw SqlClientException.FromClientException(exception);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
