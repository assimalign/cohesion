using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// A composed database application: the engines it serves and the lifecycle that
/// integrates them (and the endpoint, when one is composed) with the Cohesion
/// hosting model. Built from an <see cref="IDatabaseApplicationBuilder"/>.
/// </summary>
public interface IDatabaseApplication
{
    /// <summary>
    /// Gets the database engines this application serves, in registration order.
    /// </summary>
    IReadOnlyList<IDatabaseEngine> Engines { get; }

    /// <summary>
    /// Starts the application: engines first, then any composed services, then the
    /// endpoint.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the application in reverse order: the endpoint drains first and the
    /// engines stop last, flushing pending state durably.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task StopAsync(CancellationToken cancellationToken = default);
}
