using System;

using Assimalign.Cohesion.Database.KeyValuePair.Storage;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.KeyValuePair.Internal;

/// <summary>
/// Binds the shared version ledger to key-value entries and their existing packed locations.
/// </summary>
internal sealed class KeyValueTransactionRecordSpace : TransactionRecordSpace
{
    private readonly KeyValueStorage _storage;

    internal KeyValueTransactionRecordSpace(KeyValueStorage storage)
    {
        _storage = storage;
    }

    /// <inheritdoc />
    protected override ReadOnlyMemory<byte> ReadCore(PageId pageId, int slotIndex)
        => _storage.ReadEntry(pageId, slotIndex);

    /// <inheritdoc />
    protected override void UpdateCore(StorageTransaction transaction, PageId pageId, int slotIndex, ReadOnlySpan<byte> record)
        => _storage.UpdateEntry(transaction, pageId, slotIndex, record);

    /// <inheritdoc />
    protected override void DeleteCore(StorageTransaction transaction, PageId pageId, int slotIndex)
        => _storage.DeleteEntry(transaction, pageId, slotIndex);

    /// <inheritdoc />
    protected override ulong PackLocationCore(PageId pageId, int slotIndex)
        => KeyValueRecordLocation.Pack(pageId, slotIndex);

    /// <inheritdoc />
    protected override (PageId PageId, int SlotIndex) UnpackLocationCore(ulong location)
        => KeyValueRecordLocation.Unpack(location);
}
