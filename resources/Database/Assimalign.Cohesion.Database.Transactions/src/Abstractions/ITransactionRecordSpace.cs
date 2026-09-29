using System;

namespace Assimalign.Cohesion.Database.Transactions;

using Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Adapts an engine's stamped record layout and location encoding to the shared
/// record-space version store.
/// </summary>
/// <remarks>
/// Every record returned here or by the associated storage's unit iterator starts
/// with the <see cref="RecordVersionStamp"/> prefix. Updates of that prefix must
/// preserve the record's location; deletions physically remove the version.
/// </remarks>
public interface ITransactionRecordSpace
{
    /// <summary>
    /// Reads a complete stamped record at the given physical location.
    /// </summary>
    /// <param name="pageId">The page holding the record.</param>
    /// <param name="slotIndex">The record's slot within that page.</param>
    /// <returns>The stamp prefix and model-specific payload.</returns>
    /// <exception cref="StorageException">The record is not available.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The slot no longer exists.</exception>
    ReadOnlyMemory<byte> Read(PageId pageId, int slotIndex);

    /// <summary>
    /// Replaces a record with a same-length stamped record inside a storage bracket.
    /// </summary>
    /// <param name="transaction">The active storage bracket.</param>
    /// <param name="pageId">The page holding the record.</param>
    /// <param name="slotIndex">The record's slot within that page.</param>
    /// <param name="record">The complete replacement record.</param>
    void Update(IStorageTransaction transaction, PageId pageId, int slotIndex, ReadOnlySpan<byte> record);

    /// <summary>
    /// Physically deletes a version inside a storage bracket.
    /// </summary>
    /// <param name="transaction">The active storage bracket.</param>
    /// <param name="pageId">The page holding the record.</param>
    /// <param name="slotIndex">The record's slot within that page.</param>
    void Delete(IStorageTransaction transaction, PageId pageId, int slotIndex);

    /// <summary>
    /// Encodes a record's physical location as a ledger entry identifier.
    /// </summary>
    /// <param name="pageId">The page holding the record.</param>
    /// <param name="slotIndex">The record's slot within that page.</param>
    /// <returns>The engine's reversible location encoding.</returns>
    ulong PackLocation(PageId pageId, int slotIndex);

    /// <summary>
    /// Decodes a ledger entry identifier into its physical record location.
    /// </summary>
    /// <param name="location">The encoded location.</param>
    /// <returns>The page and slot encoded by <see cref="PackLocation"/>.</returns>
    (PageId PageId, int SlotIndex) UnpackLocation(ulong location);
}
