using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Transactions;

using Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Supplies stamp-verified logical undo for an engine's index entries without
/// coupling the transaction substrate to an index model or key type.
/// </summary>
public interface IRecordVersionIndex
{
    /// <summary>
    /// Physically erases an entry only when its current writer matches the
    /// aborted writer; an absent or differently stamped entry is unchanged.
    /// </summary>
    /// <param name="transaction">The active storage bracket.</param>
    /// <param name="key">The encoded index key copied when the effect was recorded.</param>
    /// <param name="entryReference">The entry's record reference.</param>
    /// <param name="writer">The aborted writer whose entry may be erased.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task representing completion of the index mutation.</returns>
    ValueTask EraseAsync(IStorageTransaction transaction, ReadOnlyMemory<byte> key, ulong entryReference, TransactionSequence writer, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears an entry's deleter only when it matches the aborted writer; an
    /// absent or differently stamped entry is unchanged.
    /// </summary>
    /// <param name="transaction">The active storage bracket.</param>
    /// <param name="key">The encoded index key copied when the effect was recorded.</param>
    /// <param name="entryReference">The entry's record reference.</param>
    /// <param name="writer">The aborted writer whose tombstone may be cleared.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task representing completion of the index mutation.</returns>
    ValueTask ClearDeleterAsync(IStorageTransaction transaction, ReadOnlyMemory<byte> key, ulong entryReference, TransactionSequence writer, CancellationToken cancellationToken = default);
}
