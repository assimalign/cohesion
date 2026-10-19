using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Storage.Internal;

/// <summary>
/// What recovery found and did (<see cref="StorageRecovery.Run"/>).
/// </summary>
/// <param name="MaxSequence">The highest transaction sequence in the journal; zero when it is empty.</param>
/// <param name="CheckpointLsn">The LSN of the checkpoint record the journal starts with; zero when it has none.</param>
/// <param name="RebuiltPages">
/// The pages the replay rebuilt and wrote, each stamped with the LSN of its last record. Open
/// reads every other page's LSN from the data file to place the redo point above it (invariant P,
/// Storage DESIGN.md "Recovery replay rules").
/// </param>
internal readonly record struct StorageRecoveryResult(long MaxSequence, long CheckpointLsn, IReadOnlySet<long> RebuiltPages);
