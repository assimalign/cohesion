using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Performs startup and runtime recovery from WAL and checkpoint state.
/// </summary>
public interface IStorageRecoveryManager
{
    /// <summary>
    /// Runs startup recovery.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing recovery completion.</returns>
    Task RecoverAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Triggers an online checkpoint.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing checkpoint completion.</returns>
    Task CheckpointAsync(CancellationToken cancellationToken = default);
}
