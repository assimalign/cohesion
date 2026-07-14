using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Database.Client;

namespace Assimalign.Cohesion.Database.KeyValuePair.Client;

/// <summary>
/// Options composing a key-value client: connection settings, the transport
/// driver that dials the server, and an optional telemetry observer.
/// </summary>
public sealed class KeyValueClientOptions
{
    /// <summary>
    /// Gets or sets the connection settings (database, principal, endpoint, pool
    /// size). Build them with <see cref="DatabaseConnectionSettings.Parse"/> or
    /// compose them directly.
    /// </summary>
    public DatabaseConnectionSettings? Settings { get; set; }

    /// <summary>
    /// Gets or sets the transport factory used to dial the server (TCP, named pipe,
    /// in-memory, …). Drivers are composed statically — they are never named in a
    /// connection string.
    /// </summary>
    public IConnectionFactory? ConnectionFactory { get; set; }

    /// <summary>
    /// Gets or sets an optional telemetry observer invoked around every command.
    /// </summary>
    public IKeyValueClientObserver? Observer { get; set; }
}
