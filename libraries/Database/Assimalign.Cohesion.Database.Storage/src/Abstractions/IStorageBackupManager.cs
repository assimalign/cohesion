using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Creates and restores consistent database backups.
/// </summary>
public interface IStorageBackupManager
{
    /// <summary>
    /// Creates a backup artifact.
    /// </summary>
    /// <param name="targetPath">Target backup path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Backup identifier.</returns>
    ValueTask<string> CreateBackupAsync(string targetPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores from a backup artifact.
    /// </summary>
    /// <param name="backupId">Backup identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing restore completion.</returns>
    Task RestoreBackupAsync(string backupId, CancellationToken cancellationToken = default);
}
