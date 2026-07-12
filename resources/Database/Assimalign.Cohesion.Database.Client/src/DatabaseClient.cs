using System;

namespace Assimalign.Cohesion.Database.Client;

/// <summary>
/// Creates database client instances from options.
/// </summary>
public static class DatabaseClient
{
    /// <summary>
    /// Creates a pooling client from options. Connections dial lazily — creation
    /// performs no I/O.
    /// </summary>
    /// <param name="options">The composition options. Requires settings with a database and endpoint, and a connection factory.</param>
    /// <returns>The client.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when the options carry no settings, no connection factory, no database name, no endpoint, or a non-positive pool size.</exception>
    public static IDatabaseClient Create(DatabaseClientOptions options)
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
        if (string.IsNullOrWhiteSpace(options.Settings.Database))
        {
            throw new ArgumentException("The settings must name a database.", nameof(options));
        }
        if (options.Settings.EndPoint is null)
        {
            throw new ArgumentException("The settings must carry an endpoint (an Endpoint=host[:port] connection string key, or a typed EndPoint).", nameof(options));
        }
        if (options.Settings.MaxPoolSize <= 0)
        {
            throw new ArgumentException("The pool size must be positive.", nameof(options));
        }

        return new DefaultDatabaseClient(options.Settings, options.ConnectionFactory);
    }
}
