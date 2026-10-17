using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Indexing;

/// <summary>
/// Binds an index's physical undo operations to the shared record-space version ledger.
/// </summary>
/// <remarks>
/// Keeps index keys and trees in Indexing while Transactions records opaque encoded
/// keys and invokes stamp-checked undo through <see cref="RecordVersionIndex"/>. Named for
/// the B-tree it undoes through; it was <c>RecordVersionIndex</c> until that name passed to the
/// Transactions base it derives from (#1258).
/// </remarks>
public sealed class BTreeRecordVersionIndex : RecordVersionIndex
{
    private readonly BTreeIndex _index;

    // Private: the static factory is the one way in (database-area.md, rule 1; owner decision 27
    // of 2026-10-06).
    private BTreeRecordVersionIndex(BTreeIndex index)
    {
        _index = index;
    }

    /// <summary>
    /// Creates a binding to the index whose versions the ledger tracks.
    /// </summary>
    /// <param name="index">The index to undo through.</param>
    /// <returns>The binding.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="index"/> is null.</exception>
    public static BTreeRecordVersionIndex Create(BTreeIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        return new BTreeRecordVersionIndex(index);
    }

    /// <inheritdoc />
    protected override ValueTask EraseCoreAsync(
        StorageTransaction transaction,
        ReadOnlyMemory<byte> key,
        ulong entryReference,
        TransactionSequence writer,
        CancellationToken cancellationToken)
        => _index.EraseAsync(transaction, new IndexKey(key), entryReference, writer, cancellationToken);

    /// <inheritdoc />
    protected override ValueTask ClearDeleterCoreAsync(
        StorageTransaction transaction,
        ReadOnlyMemory<byte> key,
        ulong entryReference,
        TransactionSequence writer,
        CancellationToken cancellationToken)
        => _index.ClearDeleterAsync(transaction, new IndexKey(key), entryReference, writer, cancellationToken);
}
