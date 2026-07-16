using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Client;

namespace Assimalign.Cohesion.Database.KeyValuePair.Client;

/// <summary>
/// A pooling key-value client: hands out typed connections to one key-value
/// database on one server, reusing authenticated sessions across connects.
/// </summary>
/// <remarks>
/// The typed key-value surface layers over the shared <see cref="IDatabaseClient"/>
/// core — it adds point/range operations with etag-conditional writes, a
/// key-value-scoped error surface, and a telemetry hook, and delegates pooling,
/// framing, and the wire handshake to the core. Disposing the client closes
/// every pooled connection.
/// </remarks>
public interface IKeyValueClient : IAsyncDisposable
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
    /// <exception cref="KeyValueClientException">Thrown when dialing or the handshake fails.</exception>
    ValueTask<IKeyValueConnection> ConnectAsync(CancellationToken cancellationToken = default);
}
