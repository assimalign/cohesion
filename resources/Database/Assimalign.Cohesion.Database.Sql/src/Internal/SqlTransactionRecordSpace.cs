using System;

using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// Binds the shared version ledger to SQL rows and their existing packed locations.
/// </summary>
internal sealed class SqlTransactionRecordSpace : ITransactionRecordSpace
{
    private readonly SqlStorage _storage;

    internal SqlTransactionRecordSpace(SqlStorage storage)
    {
        _storage = storage;
    }

    /// <inheritdoc />
    public ReadOnlyMemory<byte> Read(PageId pageId, int slotIndex)
        => _storage.ReadRow(pageId, slotIndex);

    /// <inheritdoc />
    public void Update(IStorageTransaction transaction, PageId pageId, int slotIndex, ReadOnlySpan<byte> record)
        => _storage.UpdateRow(transaction, pageId, slotIndex, record);

    /// <inheritdoc />
    public void Delete(IStorageTransaction transaction, PageId pageId, int slotIndex)
        => _storage.DeleteRow(transaction, pageId, slotIndex);

    /// <inheritdoc />
    public ulong PackLocation(PageId pageId, int slotIndex)
        => SqlRecordLocation.Pack(pageId, slotIndex);

    /// <inheritdoc />
    public (PageId PageId, int SlotIndex) UnpackLocation(ulong location)
        => SqlRecordLocation.Unpack(location);
}
