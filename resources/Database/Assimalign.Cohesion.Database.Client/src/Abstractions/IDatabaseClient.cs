using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Client;

/// <summary>
/// A pooling database client: rents authenticated connections to one database on
/// one server, reusing sessions across rents.
/// </summary>
/// <remarks>
/// Renting returns an open, authenticated <see cref="IDatabaseConnection"/>;
/// disposing a rented connection returns it to the pool (healthy connections keep
/// their server session alive for the next rent). Disposing the client closes
/// every pooled connection.
/// </remarks>
public interface IDatabaseClient : IAsyncDisposable
{
    /// <summary>
    /// Gets the connection settings the client was composed with.
    /// </summary>
    DatabaseConnectionSettings Settings { get; }

    /// <summary>
    /// Rents an open, authenticated connection from the pool, dialing and
    /// handshaking a new one when no pooled connection is idle and the pool is
    /// under its size limit. Waits when the pool is exhausted.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation, including the wait for a free slot.</param>
    /// <returns>An open connection; dispose it to return it to the pool.</returns>
    /// <exception cref="DatabaseClientException">Thrown when dialing or the handshake fails.</exception>
    ValueTask<IDatabaseConnection> RentAsync(CancellationToken cancellationToken = default);
}
