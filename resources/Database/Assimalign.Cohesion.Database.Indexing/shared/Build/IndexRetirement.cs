using System;
using System.Threading;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

// Deviates from the repo namespace-matches-assembly rule: stateless Indexing retirement
// orchestration is compiled into each model; no instance crosses an assembly boundary.
namespace Assimalign.Cohesion.Database.Indexing.Internal;

internal static class IndexRetirement
{
    internal const int PageBudget = 64;

    // Covers the size trigger and admission pause only. Callers must also check
    // their engine's pending checkpoint signal and interval before another batch.
    internal static bool MustYield(Storage.Storage storage)
        => storage.IsCheckpointDue(TimeSpan.MaxValue) ||
            TransactionCheckpointInterlock.ForStorage(storage).IsAdmissionPaused;

    internal static bool TryFreePages(Storage.Storage storage, ulong owner, int budget,
        CancellationToken token, out int freed, Action? beforeCommit = null)
    {
        freed = 0;
        token.ThrowIfCancellationRequested();
        if (MustYield(storage)) { return false; }
        // Caller holds the coordinator apply gate (maintenance) or a raw admission
        // lease (open-time orphan cleanup). IndexPrune does not mark statement reentrancy.
        using var bracket = storage.BeginTransaction("IndexReclamation", unchecked((long)owner));
        freed = storage.FreeOwnerPages(bracket, owner, budget);
        beforeCommit?.Invoke();
        token.ThrowIfCancellationRequested();
        bracket.Commit();
        return true;
    }
}
