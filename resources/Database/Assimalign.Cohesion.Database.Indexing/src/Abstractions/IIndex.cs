using System;
using System.Threading;
using System.Threading.Tasks;

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
}
