using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Mapping;

/// <summary>Tracks mappings and saves their changes in one atomic store transaction.</summary>
/// <typeparam name="TTransaction">The shared adapter transaction contract.</typeparam>
/// <remarks>Use one registration per logical mapping. This is a single-caller scope; it owns no
/// store resources between saves. Relationships, ordering dependencies and concurrency tokens belong
/// to adapters. Registrations and their entries are visited in registration and attachment order.
/// An unknown commit outcome permanently faults this scope and its tracked sets.</remarks>
public interface IMappingUnitOfWork<TTransaction> where TTransaction : class, IMappingTransaction
{
    /// <summary>Registers an explicitly supplied mapping and creates its identity space.</summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <typeparam name="TKey">The identity type.</typeparam>
    /// <typeparam name="TSnapshot">The detached snapshot type.</typeparam>
    /// <param name="mapper">The statically bound mapper.</param>
    /// <param name="writer">The adapter that stages changes.</param>
    /// <param name="keyComparer">The stable, non-throwing identity comparer, or the default typed comparer.</param>
    /// <returns>The mapping's tracked entities.</returns>
    /// <exception cref="ArgumentNullException">The mapper or writer is null.</exception>
    /// <exception cref="InvalidOperationException">A save is active or a previous commit has an unknown outcome.</exception>
    ITrackedEntities<TEntity, TKey> Register<TEntity, TKey, TSnapshot>(
        IEntityMapper<TEntity, TKey, TSnapshot> mapper,
        IEntityChangeWriter<TKey, TSnapshot, TTransaction> writer,
        IEqualityComparer<TKey>? keyComparer = null) where TEntity : class where TKey : notnull;

    /// <summary>Detects changes, stages them in one transaction, and accepts snapshots only after commit.</summary>
    /// <param name="cancellationToken">Cancels before atomic publication.</param>
    /// <returns>The number of inserted, updated, and deleted entities.</returns>
    /// <exception cref="InvalidOperationException">A save is active, a tracked key changed, or a previous commit has an unknown outcome.</exception>
    /// <exception cref="MappingCommitOutcomeUnknownException">The store cannot confirm publication; this scope becomes unusable.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested before commit.</exception>
    /// <remarks>Failures before commit preserve tracking for retry. A disposal failure after commit
    /// propagates with saved snapshots already accepted, so retry does not replay the committed changes.
    /// An unknown commit outcome retains unresolved snapshots and permanently prevents further use;
    /// creating another scope is safe only after authoritative reconciliation.
    /// Adapter exceptions propagate. No transaction
    /// is opened for a no-op save. Entity mutation during save is unsupported; detached snapshots
    /// nevertheless prevent later mutations from changing the values already staged.</remarks>
    ValueTask<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
