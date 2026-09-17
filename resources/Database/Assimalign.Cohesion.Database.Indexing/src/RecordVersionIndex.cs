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
/// keys and invokes stamp-checked undo through <see cref="IRecordVersionIndex"/>.
/// </remarks>
public sealed class RecordVersionIndex : IRecordVersionIndex
{
    private readonly IIndex _index;

    /// <summary>
    /// Initializes a binding to the index whose versions the ledger tracks.
    /// </summary>
    /// <param name="index">The index to undo through.</param>
    public RecordVersionIndex(IIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        _index = index;
    }

    /// <inheritdoc />
    public ValueTask EraseAsync(
        IStorageTransaction transaction,
        ReadOnlyMemory<byte> key,
        ulong entryReference,
        TransactionSequence writer,
        CancellationToken cancellationToken = default)
        => _index.EraseAsync(transaction, new IndexKey(key), entryReference, writer, cancellationToken);

    /// <inheritdoc />
    public ValueTask ClearDeleterAsync(
        IStorageTransaction transaction,
        ReadOnlyMemory<byte> key,
        ulong entryReference,
        TransactionSequence writer,
        CancellationToken cancellationToken = default)
        => _index.ClearDeleterAsync(transaction, new IndexKey(key), entryReference, writer, cancellationToken);
}
