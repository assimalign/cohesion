using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>Transaction-owned serialization and freshness contract for the MERGE stage.</summary>
internal static class GraphMergeConcurrency
{
    /// <summary>
    /// The probe must enumerate the complete whole-pattern match, including statement overlays,
    /// at the supplied view, and return every persisted entity used by those matches. It must
    /// neither mutate nor acquire locks. It owns ordinary query work/memory accounting. Bound
    /// entities are checked even when the rest of the pattern has no match. The returned matches
    /// remain protected until the transaction ends; an empty list authorizes the create branch.
    /// Call inside GraphDatabase.RunAsync (or its streaming equivalent), so a failure aborts the
    /// statement's transaction and releases its locks through the coordinator's normal end path.
    /// </summary>
    internal static ValueTask<GraphMergeRead<T>> MatchAsync<T>(GraphDatabase database,
        GraphOperation operation, GraphMergePatternIdentity identity,
        Func<TransactionSnapshot, CancellationToken, ValueTask<GraphMergeRead<T>>> probe,
        CancellationToken cancellationToken = default, Func<ValueTask>? beforePatternLock = null,
        Func<ValueTask>? beforePatternGrant = null, Action<bool>? onPatternWait = null,
        Func<ValueTask>? beforeTracker = null, Func<ValueTask>? beforeParentCheck = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return MatchCoreAsync(database, operation, identity, null, null, probe, cancellationToken, beforePatternLock, beforePatternGrant, onPatternWait, beforeTracker, beforeParentCheck);
    }

    // The key identifies one immutable, fully evaluated pattern. A fresh row evaluation must
    // supply a fresh key when any bound id or equality changes. Cache only the latest key.
    internal static ValueTask<GraphMergeRead<T>> MatchAsync<T>(GraphDatabase database,
        GraphOperation operation, object evaluatedPattern, Func<GraphMergePatternIdentity> createIdentity,
        Func<TransactionSnapshot, CancellationToken, ValueTask<GraphMergeRead<T>>> probe,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evaluatedPattern);
        ArgumentNullException.ThrowIfNull(createIdentity);
        return MatchCoreAsync(database, operation, null, evaluatedPattern, createIdentity, probe, cancellationToken, null, null, null, null, null);
    }

    private static async ValueTask<GraphMergeRead<T>> MatchCoreAsync<T>(GraphDatabase database,
        GraphOperation operation, GraphMergePatternIdentity? identity, object? evaluatedPattern,
        Func<GraphMergePatternIdentity>? createIdentity,
        Func<TransactionSnapshot, CancellationToken, ValueTask<GraphMergeRead<T>>> probe,
        CancellationToken cancellationToken, Func<ValueTask>? beforePatternLock, Func<ValueTask>? beforePatternGrant, Action<bool>? onPatternWait,
        Func<ValueTask>? beforeTracker, Func<ValueTask>? beforeParentCheck)
    {
        ArgumentNullException.ThrowIfNull(probe);
        operation.EnsureMergeActive();
        cancellationToken.ThrowIfCancellationRequested();
        // Order: session transaction admission -> database writer -> logical pattern lock ->
        // read-only index/directory/page latches. No latch spans a lock wait or probe callback.
        // Later mutations use their existing mutation gate -> (reentrant) writer -> apply ->
        // B+Tree -> page order. Never weaken the writer lock: differently expressed overlapping
        // patterns need it, and ordinary CREATE/SET/DELETE must also serialize with this check.
        await database.LockWriterAsync(operation.Context, cancellationToken).ConfigureAwait(false);
        try
        {
            var statement = operation.Merge;
            identity ??= statement.Identity(evaluatedPattern!, createIdentity!);
            // Instance-free test gate for the end-between-grants interleaving. Production
            // callers omit it; no field or allocation is added to ordinary operations.
            if (beforePatternLock is not null) { await beforePatternLock().ConfigureAwait(false); }
            // Teardown may have fenced and disposed the cached tracker during the callback.
            // Re-read the ended sentinel before touching it and after any late grant.
            statement = operation.Merge;
            if (beforeTracker is not null) { await beforeTracker().ConfigureAwait(false); }
            var grant = statement.Locks.AcquireAsync(database, operation, identity.Resource, cancellationToken, beforePatternGrant, beforeParentCheck);
            bool waited = !grant.IsCompletedSuccessfully;
            onPatternWait?.Invoke(waited);
            await grant.ConfigureAwait(false);
            statement = operation.Merge;
            operation.EnsureMergeActive();
            cancellationToken.ThrowIfCancellationRequested();
            await statement.RefreshAsync(waited, cancellationToken).ConfigureAwait(false);

            var snapshot = operation.Context.Snapshot;
            var decision = await probe(snapshot, cancellationToken).ConfigureAwait(false);
            operation.EnsureMergeActive();
            cancellationToken.ThrowIfCancellationRequested();
            if (operation.Context.IsolationLevel == IsolationLevel.ReadCommitted) { return decision; }

            var latest = statement.Latest;
            foreach (var entity in identity.BoundEntities) { CheckVersion(entity); }
            foreach (var entity in decision.Entities) { CheckVersion(entity); }
            GraphMergeRead<T> committed;
            var previous = operation.Updates.OverrideReadSnapshot(latest);
            try { committed = await probe(latest, cancellationToken).ConfigureAwait(false); }
            finally { operation.Updates.OverrideReadSnapshot(previous); }
            operation.EnsureMergeActive();
            cancellationToken.ThrowIfCancellationRequested();
            if (committed.Matches.Count < decision.Matches.Count ||
                decision.Matches.Count == 0 && committed.Matches.Count != 0) { throw Conflict(); }
            return decision;

            void CheckVersion(GraphMergeEntity entity)
            {
                // Own uncommitted versions are visible to both views. Revision comparison detects
                // replacements even when values were restored; deletion reads as an absent version.
                cancellationToken.ThrowIfCancellationRequested();
                operation.QueryBudget?.Consume();
                var before = database.Store.MergeVersion(entity.Node, entity.Id, snapshot);
                if (before != database.Store.MergeVersion(entity.Node, entity.Id, latest)) { throw Conflict(); }
            }
        }
        catch (Exception failure)
        {
            // An end can release its locks between the writer grant and the pattern grant.
            // Release a late grant for an untracked owner. For a tracked writer the manager
            // retains ownership until abort/undo completes, exactly as LockWriterAsync does.
            database.Coordinator.LockManager.ReleaseAll(operation.Context.Sequence);
            // A disposed read can fail inside a probe before its next active-state check.
            if (failure is ObjectDisposedException && operation.MergeHasEnded) { throw GraphOperation.Ended(); }
            throw;
        }
    }

    private static DatabaseTransactionAbortedException Conflict()
        => new(ErrorMessages.MergeSnapshotConflict, [new Diagnostic
        {
            Code = "CG04024", Message = ErrorMessages.MergeSnapshotConflict, Severity = DiagnosticSeverity.Error,
        }]);
}

/// <summary>
/// One complete probe result. Entities include every node/relationship in every match and
/// any other persisted entity whose value determined that result. Overlay-only identities
/// may be included: both store views return absence for them until publication.
/// </summary>
internal sealed record GraphMergeRead<T>(IReadOnlyList<T> Matches, IReadOnlyList<GraphMergeEntity> Entities);
