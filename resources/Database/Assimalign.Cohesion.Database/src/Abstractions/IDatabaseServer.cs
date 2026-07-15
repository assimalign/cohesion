using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// The network front-end for one database engine: accepts connections,
/// authenticates sessions, and pumps protocol frames into engine sessions.
/// </summary>
/// <remarks>
/// Servers are <b>per-model</b>: each fronts exactly one engine (see
/// <see cref="IDatabaseServerContext.Engine"/>) so model-specific wire behavior has
/// a home. This contract is the only area-wide server requirement — every model
/// implements it its own way, against <c>Connections</c> and the protocol child
/// root, inside its model package, carrying its own copy of the server machinery
/// (the SQL model's <c>SqlDatabaseServer</c> in
/// <c>Assimalign.Cohesion.Database.Sql</c>; the key-value model's
/// <c>KeyValueDatabaseServer</c> in
/// <c>Assimalign.Cohesion.Database.KeyValuePair</c>). "Running" lives here, not on the
/// engine: an engine is a data machine (create → use → dispose), and the server is
/// the thing that starts and stops. An <see cref="IDatabaseApplication"/> composes
/// servers generically as host services; an embedded or custom host drives
/// <see cref="StartAsync"/>/<see cref="StopAsync"/> directly.
/// </remarks>
public interface IDatabaseServer : IAsyncDisposable
{
    /// <summary>
    /// Gets the composed state of the server: the engine it fronts and the
    /// sessions currently active on it.
    /// </summary>
    IDatabaseServerContext Context { get; }

    /// <summary>
    /// Starts accepting connections.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A task that completes once the server is accepting.</returns>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops accepting connections and drains active sessions gracefully.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token bounding the drain.</param>
    /// <returns>A task that completes once the drain has finished.</returns>
    Task StopAsync(CancellationToken cancellationToken = default);
}
