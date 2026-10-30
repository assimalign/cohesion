using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Transactions;

using Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Adapts an engine's stamped record layout and location encoding to the shared
/// record-space version store.
/// </summary>
/// <remarks>
/// <para>
/// Every record returned here or by the associated storage's unit iterator starts
/// with the <see cref="RecordVersionStamp"/> prefix. Updates of that prefix must
/// preserve the record's location; deletions physically remove the version.
/// </para>
/// <para>
/// A variant set and an inverted seam: the five model storages each supply one, in their own
/// assemblies (Sql, KeyValuePair, Graph.Storage, Documents.Storage, Blob.Storage), and the
/// <see cref="TransactionCoordinator"/> drives them. The constructor is therefore
/// <c>protected</c> (<c>database-area.md</c>, rule 3). The public members are non-virtual and
/// call the protected cores a leaf implements.
/// </para>
/// </remarks>
// Deviates from the repo interface-first rule per design decision: Database engines are concrete-first — abstract bases with protected cores and sealed model leaves (owner, 2026-10-04; database-area.md).
public abstract class TransactionRecordSpace
{
    /// <summary>
    /// Initializes the record space adapter.
    /// </summary>
    protected TransactionRecordSpace()
    {
    }

    // Anonymous marker controls stay internal until XT-4 owns named/session admission.
    /// <summary>Creates an anonymous undo scope for the supplied coordinator and transaction.</summary>
    /// <param name="coordinator">The owning coordinator.</param>
    /// <param name="context">The transaction's lifecycle context.</param>
    /// <param name="cancellationToken">Cancels before admission.</param>
    /// <returns>An opaque in-memory marker.</returns>
    /// <exception cref="ArgumentNullException">A dependency is null.</exception>
    /// <exception cref="OperationCanceledException">Canceled before admission.</exception>
    /// <exception cref="Exception">A structured CK04008, CK04009 or CK04010 undo refusal; classify with GetUndoFailureCode.</exception>
    internal static async ValueTask<object> CreateUndoMarkerAsync(TransactionCoordinator coordinator, TransactionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        return await coordinator.CreateUndoMarkerAsync(context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Releases an anonymous scope and its descendants, retaining their effects.</summary>
    /// <param name="coordinator">The owning coordinator.</param>
    /// <param name="context">The transaction's lifecycle context.</param>
    /// <param name="marker">The opaque marker from creation.</param>
    /// <param name="cancellationToken">Cancels before admission.</param>
    /// <returns>A task representing completion.</returns>
    /// <exception cref="ArgumentNullException">A dependency is null.</exception>
    /// <exception cref="OperationCanceledException">Canceled before admission.</exception>
    /// <exception cref="Exception">A structured CK04008, CK04009 or CK04010 undo refusal; classify with GetUndoFailureCode.</exception>
    internal static ValueTask ReleaseUndoMarkerAsync(TransactionCoordinator coordinator, TransactionContext context, object marker, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        return coordinator.ReleaseUndoMarkerAsync(context, RequireMarker(marker), cancellationToken);
    }

    /// <summary>Undoes effects after an anonymous marker, retaining that marker for reuse.</summary>
    /// <param name="coordinator">The owning coordinator.</param>
    /// <param name="context">The transaction's lifecycle context.</param>
    /// <param name="marker">The opaque marker from creation.</param>
    /// <param name="cancellationToken">Cancels before admission; admitted undo runs to completion.</param>
    /// <returns>A task representing completion.</returns>
    /// <exception cref="ArgumentNullException">A dependency is null.</exception>
    /// <exception cref="OperationCanceledException">Canceled before admission.</exception>
    /// <exception cref="Exception">A structured CK04008, CK04009 or CK04010 undo refusal; classify with GetUndoFailureCode.</exception>
    internal static ValueTask RollbackToUndoMarkerAsync(TransactionCoordinator coordinator, TransactionContext context, object marker, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        return coordinator.RollbackToUndoMarkerAsync(context, RequireMarker(marker), cancellationToken);
    }

    /// <summary>Classifies a shared undo failure without exposing the kernel's internal exception type.</summary>
    /// <param name="exception">The failure raised by an undo control.</param>
    /// <returns>The structured CK04 identity, or null for another failure.</returns>
    internal static string? GetUndoFailureCode(Exception exception) => (exception as Internal.TransactionUndoException)?.Code;

    private static Internal.TransactionUndoMarker RequireMarker(object marker)
        => marker as Internal.TransactionUndoMarker ?? throw new Internal.TransactionUndoException("CK04008", ErrorMessages.UndoMarkerInvalid);

    /// <summary>Registers an incomplete streamed write that excludes marker controls.</summary>
    /// <param name="context">The stream's owning transaction.</param>
    /// <exception cref="ArgumentNullException">The context is null.</exception>
    protected void BeginWriteStream(TransactionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.BeginWriteStream();
    }

    /// <summary>Releases a previously registered streamed write exactly once.</summary>
    /// <param name="context">The stream's owning transaction.</param>
    /// <exception cref="ArgumentNullException">The context is null.</exception>
    /// <exception cref="InvalidOperationException">No write stream is registered.</exception>
    protected void EndWriteStream(TransactionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.EndWriteStream();
    }

    internal void UndoScopesChanged(TransactionSequence writer, bool active, RecordSpaceVersionStore ledger) => OnUndoScopesChangedCore(writer, active, ledger);
    internal void TransactionEnded(TransactionContext context) => OnTransactionEndedCore(context);
    internal void CreatedVersionUndone(PageId page, int slot, ReadOnlyMemory<byte> record) => OnCreatedVersionUndoneCore(page, slot, record);

    /// <summary>Observes marker creation/release for marker-only model ledgers.</summary>
    /// <param name="writer">The scope's root writer.</param><param name="active">Whether a marker remains.</param><param name="ledger">The shared ordered ledger.</param>
    /// <remarks>Creation must be exception-atomic; release and root-end cleanup must not throw. The kernel unwinds an unpublished marker after a creation failure.</remarks>
    protected internal virtual void OnUndoScopesChangedCore(TransactionSequence writer, bool active, RecordSpaceVersionStore ledger) { }
    /// <summary>Ends marker-only model state after the root transaction ends.</summary>
    /// <param name="context">The ended transaction.</param>
    /// <remarks>Cleanup is idempotent and must not throw; disposal can repeat an earlier end notification.</remarks>
    protected internal virtual void OnTransactionEndedCore(TransactionContext context) { }
    /// <summary>Removes a created version's model directory reference during partial or full undo.</summary>
    /// <param name="page">The version's page.</param><param name="slot">The version's slot.</param><param name="record">The version before deletion.</param>
    protected internal virtual void OnCreatedVersionUndoneCore(PageId page, int slot, ReadOnlyMemory<byte> record) { }

    /// <summary>
    /// Reads a complete stamped record at the given physical location.
    /// </summary>
    /// <param name="pageId">The page holding the record.</param>
    /// <param name="slotIndex">The record's slot within that page.</param>
    /// <returns>The stamp prefix and model-specific payload.</returns>
    /// <exception cref="StorageException">The record is not available.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The slot no longer exists.</exception>
    public ReadOnlyMemory<byte> Read(PageId pageId, int slotIndex) => ReadCore(pageId, slotIndex);

    /// <summary>
    /// Replaces a record with a same-length stamped record inside a storage bracket.
    /// </summary>
    /// <param name="transaction">The active storage bracket.</param>
    /// <param name="pageId">The page holding the record.</param>
    /// <param name="slotIndex">The record's slot within that page.</param>
    /// <param name="record">The complete replacement record.</param>
    /// <exception cref="ArgumentNullException"><paramref name="transaction"/> is null.</exception>
    public void Update(StorageTransaction transaction, PageId pageId, int slotIndex, ReadOnlySpan<byte> record)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        UpdateCore(transaction, pageId, slotIndex, record);
    }

    /// <summary>
    /// Physically deletes a version inside a storage bracket.
    /// </summary>
    /// <param name="transaction">The active storage bracket.</param>
    /// <param name="pageId">The page holding the record.</param>
    /// <param name="slotIndex">The record's slot within that page.</param>
    /// <exception cref="ArgumentNullException"><paramref name="transaction"/> is null.</exception>
    public void Delete(StorageTransaction transaction, PageId pageId, int slotIndex)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        DeleteCore(transaction, pageId, slotIndex);
    }

    /// <summary>
    /// Encodes a record's physical location as a ledger entry identifier.
    /// </summary>
    /// <param name="pageId">The page holding the record.</param>
    /// <param name="slotIndex">The record's slot within that page.</param>
    /// <returns>The engine's reversible location encoding.</returns>
    public ulong PackLocation(PageId pageId, int slotIndex) => PackLocationCore(pageId, slotIndex);

    /// <summary>
    /// Decodes a ledger entry identifier into its physical record location.
    /// </summary>
    /// <param name="location">The encoded location.</param>
    /// <returns>The page and slot encoded by <see cref="PackLocation"/>.</returns>
    public (PageId PageId, int SlotIndex) UnpackLocation(ulong location) => UnpackLocationCore(location);

    /// <summary>
    /// Reads the stamped record at the location (see <see cref="Read"/>).
    /// </summary>
    /// <param name="pageId">The page holding the record.</param>
    /// <param name="slotIndex">The record's slot within that page.</param>
    /// <returns>The stamp prefix and model-specific payload.</returns>
    protected abstract ReadOnlyMemory<byte> ReadCore(PageId pageId, int slotIndex);

    /// <summary>
    /// Replaces the record in place (see <see cref="Update"/>).
    /// </summary>
    /// <param name="transaction">The active storage bracket; never null.</param>
    /// <param name="pageId">The page holding the record.</param>
    /// <param name="slotIndex">The record's slot within that page.</param>
    /// <param name="record">The complete replacement record.</param>
    protected abstract void UpdateCore(StorageTransaction transaction, PageId pageId, int slotIndex, ReadOnlySpan<byte> record);

    /// <summary>
    /// Deletes the version (see <see cref="Delete"/>).
    /// </summary>
    /// <param name="transaction">The active storage bracket; never null.</param>
    /// <param name="pageId">The page holding the record.</param>
    /// <param name="slotIndex">The record's slot within that page.</param>
    protected abstract void DeleteCore(StorageTransaction transaction, PageId pageId, int slotIndex);

    /// <summary>
    /// Encodes a location (see <see cref="PackLocation"/>).
    /// </summary>
    /// <param name="pageId">The page holding the record.</param>
    /// <param name="slotIndex">The record's slot within that page.</param>
    /// <returns>The engine's reversible location encoding.</returns>
    protected abstract ulong PackLocationCore(PageId pageId, int slotIndex);

    /// <summary>
    /// Decodes a location (see <see cref="UnpackLocation"/>).
    /// </summary>
    /// <param name="location">The encoded location.</param>
    /// <returns>The page and slot.</returns>
    protected abstract (PageId PageId, int SlotIndex) UnpackLocationCore(ulong location);
}
