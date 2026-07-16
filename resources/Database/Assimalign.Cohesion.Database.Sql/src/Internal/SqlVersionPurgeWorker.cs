using System;
using System.Threading;

namespace Assimalign.Cohesion.Database.Sql.Internal;

using Assimalign.Cohesion.Database.Storage;

/// <summary>
/// The engine-owned MVCC version-purge worker: per pass, per open database, it
/// retries the logical undo of any aborted writer whose rollback-time purge
/// failed (<c>IVersionStore.PurgeWriterAsync</c>) and physically reclaims
/// versions no snapshot can reach — committed tombstones below the safe prune
/// bound (<c>IVersionStore.PruneAsync</c>) — so version-space amplification is
/// bounded by the oldest in-flight snapshot.
/// </summary>
/// <remarks>
/// The worker runs on the engine's own timer
/// (<see cref="SqlDatabaseEngineOptions.MaintenanceInterval"/>) — embedded and
/// hosted consumers get identical reclamation because nothing outside the
/// engine participates (R10). Passes iterate the engine's database snapshot and
/// tolerate racing a drop. A pass failure flips the engine's observational
/// state to Faulted without stopping service: unpurged versions cost space,
/// never consistency — visibility always filters them. (Aborted writers are
/// normally unlinked inline at rollback, before their locks release — the
/// worker's abort duty is the retry of a failed undo.)
/// </remarks>
internal sealed class SqlVersionPurgeWorker : DatabaseEngineWorker
{
    private readonly SqlDatabaseEngine _engine;

    internal SqlVersionPurgeWorker(SqlDatabaseEngine engine)
    {
        _engine = engine;
    }

    /// <inheritdoc />
    public override string Name => _engine.Name + "/version-purge";

    /// <inheritdoc />
    public override DatabaseEngineWorkerKind Kind => DatabaseEngineWorkerKind.VersionPurge;

    /// <inheritdoc />
    public override TimeSpan Interval => _engine.EngineOptions.MaintenanceInterval;

    /// <inheritdoc />
    public override void RunIteration(CancellationToken cancellationToken)
    {
        foreach (SqlDatabaseInstance database in _engine.GetInstanceSnapshot())
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                database.Coordinator.RunVersionPurgePass(cancellationToken);
            }
            catch (StorageTransactionException)
            {
                // A storage bracket is active on this database; retry next pass.
            }
            catch (ObjectDisposedException)
            {
                // The snapshot can race a database drop; nothing left to purge.
            }
        }
    }
}
