using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Replication;

/// <summary>
/// Coordinates logical or physical replication streams.
/// </summary>
public interface IReplicationCoordinator
{
    /// <summary>
    /// Starts replication workers.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing startup completion.</returns>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops replication workers.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing shutdown completion.</returns>
    Task StopAsync(CancellationToken cancellationToken = default);
}
