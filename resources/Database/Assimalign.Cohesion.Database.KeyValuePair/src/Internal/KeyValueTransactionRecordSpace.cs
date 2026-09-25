using System;

using Assimalign.Cohesion.Database.KeyValuePair.Storage;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.KeyValuePair.Internal;

/// <summary>
/// Binds the shared version ledger to key-value entries and their existing packed locations.
/// </summary>
internal sealed class KeyValueTransactionRecordSpace : ITransactionRecordSpace
{
    private readonly KeyValueStorage _storage;

    internal KeyValueTransactionRecordSpace(KeyValueStorage storage)
    {
        _storage = storage;
    }

    /// <inheritdoc />
    public ReadOnlyMemory<byte> Read(PageId pageId, int slotIndex)
        => _storage.ReadEntry(pageId, slotIndex);

    /// <inheritdoc />
    public void Update(IStorageTransaction transaction, PageId pageId, int slotIndex, ReadOnlySpan<byte> record)
        => _storage.UpdateEntry(transaction, pageId, slotIndex, record);

    /// <inheritdoc />
    public void Delete(IStorageTransaction transaction, PageId pageId, int slotIndex)
        => _storage.DeleteEntry(transaction, pageId, slotIndex);

    /// <inheritdoc />
    public ulong PackLocation(PageId pageId, int slotIndex)
        => KeyValueRecordLocation.Pack(pageId, slotIndex);

    /// <inheritdoc />
    public (PageId PageId, int SlotIndex) UnpackLocation(ulong location)
        => KeyValueRecordLocation.Unpack(location);
}
