using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Transactions;

using Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Supplies stamp-verified logical undo for an engine's index entries without
/// coupling the transaction substrate to an index model or key type.
/// </summary>
/// <remarks>
/// <para>
/// The version store's ledger records an index effect with the index's undo adapter and the
/// encoded key (<see cref="RecordSpaceVersionStore.RecordIndexEntryCreated"/>), and calls the
/// adapter back when the writer aborts. Transactions drives the type and never sees an index:
/// the adapters live in the assemblies that own the trees, which is why the constructor is
/// <c>protected</c> (<c>database-area.md</c>, rule 3).
/// </para>
/// <para>
/// The public members are non-virtual: they check their arguments and call the protected
/// cores, which a leaf implements.
/// </para>
/// </remarks>
// Deviates from the repo interface-first rule per design decision: Database engines are concrete-first — abstract bases with protected cores and sealed model leaves (owner, 2026-10-04; database-area.md).
public abstract class RecordVersionIndex
{
    /// <summary>
    /// Initializes the adapter.
    /// </summary>
    protected RecordVersionIndex()
    {
    }

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
    /// <exception cref="ArgumentNullException"><paramref name="transaction"/> is null.</exception>
    public ValueTask EraseAsync(StorageTransaction transaction, ReadOnlyMemory<byte> key, ulong entryReference, TransactionSequence writer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return EraseCoreAsync(transaction, key, entryReference, writer, cancellationToken);
    }

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
    /// <exception cref="ArgumentNullException"><paramref name="transaction"/> is null.</exception>
    public ValueTask ClearDeleterAsync(StorageTransaction transaction, ReadOnlyMemory<byte> key, ulong entryReference, TransactionSequence writer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return ClearDeleterCoreAsync(transaction, key, entryReference, writer, cancellationToken);
    }

    /// <summary>
    /// Erases the entry when its writer stamp is <paramref name="writer"/> (see <see cref="EraseAsync"/>).
    /// </summary>
    /// <param name="transaction">The active storage bracket; never null.</param>
    /// <param name="key">The encoded index key.</param>
    /// <param name="entryReference">The entry's record reference.</param>
    /// <param name="writer">The aborted writer.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task representing completion of the index mutation.</returns>
    protected abstract ValueTask EraseCoreAsync(StorageTransaction transaction, ReadOnlyMemory<byte> key, ulong entryReference, TransactionSequence writer, CancellationToken cancellationToken);

    /// <summary>
    /// Clears the entry's deleter when it is <paramref name="writer"/> (see <see cref="ClearDeleterAsync"/>).
    /// </summary>
    /// <param name="transaction">The active storage bracket; never null.</param>
    /// <param name="key">The encoded index key.</param>
    /// <param name="entryReference">The entry's record reference.</param>
    /// <param name="writer">The aborted writer.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task representing completion of the index mutation.</returns>
    protected abstract ValueTask ClearDeleterCoreAsync(StorageTransaction transaction, ReadOnlyMemory<byte> key, ulong entryReference, TransactionSequence writer, CancellationToken cancellationToken);
}
