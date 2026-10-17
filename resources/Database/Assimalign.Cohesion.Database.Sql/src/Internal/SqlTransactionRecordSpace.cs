using System;

using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// Binds the shared version ledger to SQL rows and their existing packed locations.
/// </summary>
internal sealed class SqlTransactionRecordSpace : TransactionRecordSpace
{
    private readonly SqlStorage _storage;

    internal SqlTransactionRecordSpace(SqlStorage storage)
    {
        _storage = storage;
    }

    /// <inheritdoc />
    protected override ReadOnlyMemory<byte> ReadCore(PageId pageId, int slotIndex)
        => _storage.ReadRow(pageId, slotIndex);

    /// <inheritdoc />
    protected override void UpdateCore(StorageTransaction transaction, PageId pageId, int slotIndex, ReadOnlySpan<byte> record)
        => _storage.UpdateRow(transaction, pageId, slotIndex, record);

    /// <inheritdoc />
    protected override void DeleteCore(StorageTransaction transaction, PageId pageId, int slotIndex)
        => _storage.DeleteRow(transaction, pageId, slotIndex);

    /// <inheritdoc />
    protected override ulong PackLocationCore(PageId pageId, int slotIndex)
        => SqlRecordLocation.Pack(pageId, slotIndex);

    /// <inheritdoc />
    protected override (PageId PageId, int SlotIndex) UnpackLocationCore(ulong location)
        => SqlRecordLocation.Unpack(location);
}
