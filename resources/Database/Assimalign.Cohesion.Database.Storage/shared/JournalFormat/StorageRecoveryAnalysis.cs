using System.Collections.Generic;

// Deviates from the repo RootNamespace rule: JournalFormat codec and analysis are compiled
// into Storage and each engine without exposing a public implementation bridge.
namespace Assimalign.Cohesion.Database.Storage.Internal;

// Identities only: no payload or per-frame entry survives the initialization scan.
internal sealed record StorageRecoveryAnalysis(
    long LastLsn,
    long MaxSequence,
    long CheckpointLsn,
    IReadOnlySet<long> Committed,
    IReadOnlyDictionary<long, StorageRecoveryPagePlan> Pages);
