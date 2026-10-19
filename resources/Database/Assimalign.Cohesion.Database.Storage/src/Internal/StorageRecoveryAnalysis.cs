using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Storage.Internal;

// Identities only: no payload or per-frame entry survives the initialization scan.
internal sealed record StorageRecoveryAnalysis(
    long LastLsn,
    long MaxSequence,
    long CheckpointLsn,
    IReadOnlySet<long> Committed,
    IReadOnlyDictionary<long, StorageRecoveryPagePlan> Pages);
