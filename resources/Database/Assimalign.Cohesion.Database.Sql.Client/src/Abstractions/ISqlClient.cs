using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Client;

namespace Assimalign.Cohesion.Database.Sql.Client;

/// <summary>
/// A pooling SQL client: hands out typed connections to one SQL database on one
/// server, reusing authenticated sessions across connects.
/// </summary>
/// <remarks>
/// The typed SQL surface layers over the shared <see cref="IDatabaseClient"/>
/// core — it adds commands, typed result sets, a SQL-scoped error surface, and a
/// telemetry hook, and delegates pooling, framing, and the wire handshake to the
/// core. Disposing the client closes every pooled connection.
/// </remarks>
public interface ISqlClient : IAsyncDisposable
{
    /// <summary>
    /// Gets the connection settings the client was composed with.
    /// </summary>
    DatabaseConnectionSettings Settings { get; }

    /// <summary>
    /// Rents an open, authenticated typed connection from the pool, dialing and
    /// handshaking a new one when none is idle and the pool is under its limit.
    /// Waits when the pool is exhausted.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation, including the wait for a free slot.</param>
    /// <returns>An open typed connection; dispose it to return it to the pool.</returns>
    /// <exception cref="SqlClientException">Thrown when dialing or the handshake fails.</exception>
    ValueTask<ISqlConnection> ConnectAsync(CancellationToken cancellationToken = default);
}
