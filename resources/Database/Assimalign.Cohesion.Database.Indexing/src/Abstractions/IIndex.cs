using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Indexing;

/// <summary>
/// A single index over an object's entries: ordered key → entry-reference mappings.
/// </summary>
/// <remarks>
/// Index mutations ride the owning transaction — they are stamped with the writing
/// transaction's sequence and become visible under the same MVCC rules as the data
/// they reference. Values are opaque entry references (typically a page address or
/// entry identity) supplied by the model's storage layer.
/// </remarks>
public interface IIndex
{
    /// <summary>
    /// Gets the name of the index, unique within its owning object.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the physical structure backing the index.
    /// </summary>
    IndexKind Kind { get; }

    /// <summary>
    /// Gets a value indicating whether the index enforces key uniqueness.
    /// </summary>
    bool IsUnique { get; }

    /// <summary>
    /// Inserts a key → entry-reference mapping.
    /// </summary>
    /// <param name="transaction">The transaction the mutation belongs to.</param>
    /// <param name="key">The key to insert.</param>
    /// <param name="entryReference">The opaque entry reference the key maps to.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <exception cref="IndexException">Thrown when the index is unique and the key already maps to a visible entry.</exception>
    ValueTask InsertAsync(ITransactionContext transaction, IndexKey key, ulong entryReference, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a key → entry-reference mapping.
    /// </summary>
    /// <param name="transaction">The transaction the mutation belongs to.</param>
    /// <param name="key">The key to delete.</param>
    /// <param name="entryReference">The entry reference to remove for the key.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    ValueTask DeleteAsync(ITransactionContext transaction, IndexKey key, ulong entryReference, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a cursor over the specified key range, positioned before the first match.
    /// </summary>
    /// <param name="transaction">The transaction whose snapshot reads resolve through.</param>
    /// <param name="range">The key range to scan.</param>
    /// <param name="reverse">Whether to scan in descending key order.</param>
    /// <returns>A cursor over the visible entries in the range.</returns>
    IIndexCursor OpenCursor(ITransactionContext transaction, IndexKeyRange range, bool reverse = false);

    /// <summary>
    /// Opens a cursor over the specified key range through an explicit visibility
    /// snapshot. This is the seam statement-scoped readers use: a statement's
    /// snapshot is captured once (per-statement under ReadCommitted), and reading
    /// through the same snapshot the row scan uses is what keeps an index seek
    /// exactly equivalent to the scan it replaces.
    /// </summary>
    /// <param name="snapshot">The visibility snapshot entries filter through.</param>
    /// <param name="range">The key range to scan.</param>
    /// <param name="reverse">Whether to scan in descending key order.</param>
    /// <returns>A cursor over the visible entries in the range.</returns>
    IIndexCursor OpenCursor(TransactionSnapshot snapshot, IndexKeyRange range, bool reverse = false);

    /// <summary>
    /// Inserts an entry carrying explicit version stamps inside the given physical
    /// write-ahead bracket — the offline (DDL-blocking) build path: an index built
    /// over existing rows preserves each stored version's writer and deleter, so
    /// snapshots older than the index read exactly what the equivalent row scan
    /// shows them. No uniqueness check is performed; the builder owns duplicate
    /// detection over the live versions it feeds in (it holds the object's
    /// exclusive lock, so no concurrent writer can race the build).
    /// </summary>
    /// <param name="transaction">The physical storage bracket the build rides.</param>
    /// <param name="key">The key to insert.</param>
    /// <param name="entryReference">The opaque entry reference the key maps to.</param>
    /// <param name="writer">The version's writer stamp, preserved from the source row.</param>
    /// <param name="deleter">The version's deleter stamp, preserved from the source row (<see cref="TransactionSequence.None"/> for live versions).</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <exception cref="IndexException">The key exceeds the maximum key length.</exception>
    ValueTask InsertVersionAsync(IStorageTransaction transaction, IndexKey key, ulong entryReference, TransactionSequence writer, TransactionSequence deleter, CancellationToken cancellationToken = default);

    /// <summary>
    /// Physically removes the entry mapping <paramref name="key"/> to
    /// <paramref name="entryReference"/> when its writer stamp equals
    /// <paramref name="writer"/> — the logical undo of an aborted writer's insert.
    /// Runs inside the given physical bracket (an undo executes while the aborting
    /// transaction still holds its locks, outside any statement bracket). A no-op
    /// when no matching entry exists (undo is idempotent by construction).
    /// </summary>
    /// <param name="transaction">The physical storage bracket the undo rides.</param>
    /// <param name="key">The key of the entry to remove.</param>
    /// <param name="entryReference">The entry reference the key maps to.</param>
    /// <param name="writer">The writer stamp the entry must carry to be removed.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    ValueTask EraseAsync(IStorageTransaction transaction, IndexKey key, ulong entryReference, TransactionSequence writer, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears the deleter stamp of the entry mapping <paramref name="key"/> to
    /// <paramref name="entryReference"/> when it equals <paramref name="deleter"/> —
    /// the logical undo of an aborted writer's tombstone. Runs inside the given
    /// physical bracket; a no-op when no matching entry exists.
    /// </summary>
    /// <param name="transaction">The physical storage bracket the undo rides.</param>
    /// <param name="key">The key of the tombstoned entry.</param>
    /// <param name="entryReference">The entry reference the key maps to.</param>
    /// <param name="deleter">The deleter stamp the entry must carry to be cleared.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    ValueTask ClearDeleterAsync(IStorageTransaction transaction, IndexKey key, ulong entryReference, TransactionSequence deleter, CancellationToken cancellationToken = default);
}
