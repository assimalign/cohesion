using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Client;

namespace Assimalign.Cohesion.Database.Graph.Client;

/// <summary>A pooled client for graph statements and path queries.</summary>
public interface IGraphClient : IAsyncDisposable
{
    /// <summary>Gets the endpoint, authentication, database, and pool settings.</summary>
    DatabaseConnectionSettings Settings { get; }

    /// <summary>Rents an authenticated connection bound to the configured database.</summary>
    /// <param name="cancellationToken">Cancellation token for connecting.</param>
    /// <returns>The rented connection; dispose it to return its session.</returns>
    /// <exception cref="GraphClientException">The handshake or transport failed.</exception>
    ValueTask<IGraphConnection> ConnectAsync(CancellationToken cancellationToken = default);
}

