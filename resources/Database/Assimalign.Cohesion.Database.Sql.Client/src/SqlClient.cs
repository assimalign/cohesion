using System;

using Assimalign.Cohesion.Database.Client;

namespace Assimalign.Cohesion.Database.Sql.Client;

/// <summary>
/// Creates SQL client instances from options.
/// </summary>
public static class SqlClient
{
    /// <summary>
    /// Creates a pooling SQL client from options. Connections dial lazily — creation
    /// performs no I/O.
    /// </summary>
    /// <param name="options">The composition options. Requires settings with a database and endpoint, and a connection factory.</param>
    /// <returns>The client.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when the options carry no settings or no connection factory.</exception>
    public static ISqlClient Create(SqlClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Settings is null)
        {
            throw new ArgumentException("Connection settings are required.", nameof(options));
        }
        if (options.ConnectionFactory is null)
        {
            throw new ArgumentException("A connection factory is required.", nameof(options));
        }

        // The shared core validates the settings and factory in full (database,
        // endpoint, pool size); let its ArgumentException surface with its message.
        IDatabaseClient client = DatabaseClient.Create(new DatabaseClientOptions
        {
            Settings = options.Settings,
            ConnectionFactory = options.ConnectionFactory,
        });

        return new DefaultSqlClient(client, options.Settings, options.Observer);
    }
}
