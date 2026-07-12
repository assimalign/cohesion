using System;

using Assimalign.Cohesion.Hosting;

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

    /// <summary>
    /// Wraps a server as an <see cref="IHostService"/> so a host can run its accept
    /// loop and drain it on shutdown per the hosting execution menu.
    /// </summary>
    /// <param name="server">The server to host.</param>
    /// <returns>An endpoint host service that starts the server on start and gracefully drains it on stop.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="server"/> is null.</exception>
    /// <remarks>
    /// The adapter lives here rather than in <c>Database.Hosting</c> because the
    /// resource hosting-isolation rule (COHRES002) bars the hosting module from
    /// referencing any same-area library except the area root — so only the server's
    /// own package can name <see cref="IDatabaseServer"/> to compose it. The hosting
    /// module composes the returned <see cref="IHostService"/> generically.
    /// </remarks>
    public static IHostService CreateHostService(IDatabaseServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return new DatabaseServerHostService(server);
    }
}
