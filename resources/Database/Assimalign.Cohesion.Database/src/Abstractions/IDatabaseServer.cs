using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// The network front-end of a database host: accepts connections, authenticates
/// sessions, and pumps protocol frames into engine sessions.
/// </summary>
/// <remarks>
/// The server is model-agnostic — it serves whichever engines the host registered.
/// It is not itself a host service: the hosting module's <c>DatabaseApplication</c>
/// wraps the server configured on its options in an internal endpoint host service,
/// and an embedded or custom host drives <see cref="StartAsync"/>/<see cref="StopAsync"/>
/// directly. The runtime implementation lives in the hosting module
/// (<c>Assimalign.Cohesion.Database.Hosting</c>); this seam lets area libraries
/// observe or compose the server without referencing the runtime.
/// </remarks>
public interface IDatabaseServer : IAsyncDisposable
{
    /// <summary>
    /// Gets the engines this server dispatches sessions to.
    /// </summary>
    IReadOnlyList<IDatabaseEngine> Engines { get; }

    /// <summary>
    /// Gets the sessions currently active on this server.
    /// </summary>
    IReadOnlyCollection<IDatabaseServerSession> Sessions { get; }

    /// <summary>
    /// Starts accepting connections.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops accepting connections and drains active sessions gracefully.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token bounding the drain.</param>
    Task StopAsync(CancellationToken cancellationToken = default);
}
