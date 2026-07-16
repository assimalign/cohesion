using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Database.Client;

/// <summary>
/// Options composing a database client: the connection settings and the
/// transport driver that dials the server.
/// </summary>
public sealed class DatabaseClientOptions
{
    /// <summary>
    /// Gets or sets the connection settings (database, principal, endpoint,
    /// pool size). Build them with <see cref="DatabaseConnectionSettings.Parse"/>
    /// or compose them directly.
    /// </summary>
    public DatabaseConnectionSettings? Settings { get; set; }

    /// <summary>
    /// Gets or sets the transport factory used to dial the server (TCP, named
    /// pipe, in-memory, …). Drivers are composed statically — they are never
    /// named in a connection string.
    /// </summary>
    public IConnectionFactory? ConnectionFactory { get; set; }
}
