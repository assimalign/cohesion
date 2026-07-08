using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Transactions;

/// <summary>
/// Stores and resolves MVCC version chains for stored entries.
/// </summary>
/// <remarks>
/// Model storage layers implement this over their page layouts: each logical entry
/// (row, document, key) heads a chain of versions stamped with the writing
/// transaction's sequence. Readers resolve the newest chain member visible through
/// their snapshot; the transaction manager's <see cref="ITransactionManager.OldestActive"/>
/// bound drives pruning of versions no snapshot can reach.
/// </remarks>
public interface IVersionStore
{
    /// <summary>
    /// Appends a new version for the specified entry, stamped with the writing
    /// transaction's sequence.
    /// </summary>
    /// <param name="objectId">The identity of the containing object (table, collection, container).</param>
    /// <param name="entryId">The identity of the entry within the object.</param>
    /// <param name="payload">The version payload.</param>
    /// <param name="writer">The sequence of the writing transaction.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    ValueTask AppendVersionAsync(ulong objectId, ulong entryId, ReadOnlyMemory<byte> payload, TransactionSequence writer, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the newest version of the specified entry visible through the snapshot.
    /// </summary>
    /// <param name="objectId">The identity of the containing object.</param>
    /// <param name="entryId">The identity of the entry within the object.</param>
    /// <param name="snapshot">The snapshot visibility is resolved against.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The visible version payload, or null when no visible version exists.</returns>
    ValueTask<ReadOnlyMemory<byte>?> GetVisibleVersionAsync(ulong objectId, ulong entryId, TransactionSnapshot snapshot, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes versions that no active or future snapshot can reach.
    /// </summary>
    /// <param name="oldestActive">The oldest transaction sequence still active.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The number of versions pruned.</returns>
    ValueTask<long> PruneAsync(TransactionSequence oldestActive, CancellationToken cancellationToken = default);
}
