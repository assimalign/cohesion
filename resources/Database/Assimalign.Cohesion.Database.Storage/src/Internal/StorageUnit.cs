using System;

namespace Assimalign.Cohesion.Database.Storage;

/// <summary>
/// A read-only snapshot of a storage unit (record) with its page location and data.
/// </summary>
internal readonly struct StorageUnit : IStorageUnit
{
    internal StorageUnit(PageId pageId, int slotIndex, ReadOnlyMemory<byte> data)
    {
        PageId = pageId;
        SlotIndex = slotIndex;
        Data = data;
    }

    /// <inheritdoc />
    public PageId PageId { get; }

    /// <inheritdoc />
    public int SlotIndex { get; }

    /// <inheritdoc />
    public ReadOnlyMemory<byte> Data { get; }
}
