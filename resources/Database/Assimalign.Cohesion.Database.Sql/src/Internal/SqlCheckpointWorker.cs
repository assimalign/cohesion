using System;
using System.Threading;

namespace Assimalign.Cohesion.Database.Sql.Internal;

using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Storage;

/// <summary>
/// The engine-owned checkpointer: periodically checkpoints every open storage file
/// set — both the data set and the <c>.catalog</c> set of each database — durably
/// flushing all page state and truncating the journal with continued LSNs (the WAL
/// v2 invariant: LSNs never restart across truncation).
/// </summary>
/// <remarks>
/// A checkpoint requires no active transactions on the storage; a busy storage
/// throws <see cref="StorageTransactionException"/> and is simply retried on the
/// next pass — checkpointing is opportunistic, never blocking foreground work.
/// </remarks>
internal sealed class SqlCheckpointWorker : DatabaseEngineWorker
{
    private readonly SqlDatabaseEngine _engine;

    internal SqlCheckpointWorker(SqlDatabaseEngine engine)
    {
        _engine = engine;
    }

    /// <inheritdoc />
    public override string Name => _engine.Name + "/checkpoint";

    /// <inheritdoc />
    public override DatabaseEngineWorkerKind Kind => DatabaseEngineWorkerKind.Checkpoint;

    /// <inheritdoc />
    public override TimeSpan Interval => _engine.EngineOptions.CheckpointInterval;

    /// <inheritdoc />
    public override void RunIteration(CancellationToken cancellationToken)
    {
        foreach (SqlStorage storage in _engine.GetStorageSnapshot())
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                storage.Checkpoint();
            }
            catch (StorageTransactionException)
            {
                // A transaction is active on this storage; retry on the next pass.
            }
            catch (ObjectDisposedException)
            {
                // The snapshot can race a database drop; nothing left to checkpoint.
            }
        }
    }
}
