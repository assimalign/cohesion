using System;

namespace Assimalign.Cohesion.Database.Server;

/// <summary>
/// Creates database server instances from options.
/// </summary>
public static class DatabaseServer
{
    /// <summary>
    /// Creates a new server over the options' listener and engines. The server is
    /// inert until <see cref="IDatabaseServer.StartAsync"/> is called.
    /// </summary>
    /// <param name="options">The composition options. Requires a bound <see cref="DatabaseServerOptions.Listener"/> and at least one engine.</param>
    /// <returns>The server.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when the options carry no listener, no engines, or a non-positive session limit.</exception>
    public static IDatabaseServer Create(DatabaseServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Listener is null)
        {
            throw new ArgumentException("A bound connection listener is required.", nameof(options));
        }
        if (options.Engines.Count == 0)
        {
            throw new ArgumentException("At least one database engine is required.", nameof(options));
        }
        if (options.MaxSessions <= 0)
        {
            throw new ArgumentException("The session limit must be positive.", nameof(options));
        }

        return new DefaultDatabaseServer(options);
    }
}
