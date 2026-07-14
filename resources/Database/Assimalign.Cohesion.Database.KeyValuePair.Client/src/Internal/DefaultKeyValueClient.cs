using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Client;

namespace Assimalign.Cohesion.Database.KeyValuePair.Client;

/// <summary>
/// The default key-value client: a thin typed facade over the shared pooling
/// client core. It delegates dialing, the handshake, and pooling to the core and
/// wraps each rented connection in a typed <see cref="KeyValueConnection"/>.
/// </summary>
internal sealed class DefaultKeyValueClient : IKeyValueClient
{
    private readonly IDatabaseClient _client;
    private readonly IKeyValueClientObserver? _observer;

    internal DefaultKeyValueClient(IDatabaseClient client, DatabaseConnectionSettings settings, IKeyValueClientObserver? observer)
    {
        _client = client;
        Settings = settings;
        _observer = observer;
    }

    /// <inheritdoc />
    public DatabaseConnectionSettings Settings { get; }

    /// <inheritdoc />
    public async ValueTask<IKeyValueConnection> ConnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            IDatabaseConnection connection = await _client.RentAsync(cancellationToken).ConfigureAwait(false);
            return new KeyValueConnection(connection, _observer);
        }
        catch (DatabaseClientException exception)
        {
            throw KeyValueClientException.FromClientException(exception);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
