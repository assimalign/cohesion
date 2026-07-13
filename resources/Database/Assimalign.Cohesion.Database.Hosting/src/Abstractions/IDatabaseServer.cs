using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Hosting;

/// <summary>
/// The network front-end of a database host: accepts connections, authenticates
/// sessions, and pumps protocol frames into engine sessions.
/// </summary>
/// <remarks>
/// The server is model-agnostic — it serves whichever engines the host registered.
/// It is not itself an <c>IHostService</c>: <see cref="DatabaseApplication"/> wraps
/// the server assigned to <see cref="DatabaseApplicationOptions.Server"/> in its
/// internal endpoint host service, and an embedded or custom host drives
/// <see cref="StartAsync"/>/<see cref="StopAsync"/> directly.
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
