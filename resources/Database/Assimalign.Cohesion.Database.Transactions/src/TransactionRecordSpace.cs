using System;

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
