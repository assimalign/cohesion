using System;

using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// Binds the shared version ledger to SQL rows and their existing packed locations.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("SqlTransactionRecordSpace")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlTransactionRecordSpace.DebugView))]
internal sealed class SqlTransactionRecordSpace : TransactionRecordSpace
{
    private readonly SqlStorage _storage;

    internal SqlTransactionRecordSpace(SqlStorage storage)
    {
        _storage = storage;
    }

    protected override void OnUndoScopesChangedCore(TransactionSequence writer, bool active, RecordSpaceVersionStore ledger) => _storage.RowVersionLinks.SetUndoScope(writer, active, ledger);
    protected override void OnTransactionEndedCore(TransactionContext context) => _storage.RowVersionLinks.EndUndoScope(context);

    /// <inheritdoc />
    protected override ReadOnlyMemory<byte> ReadCore(PageId pageId, int slotIndex)
        => _storage.ReadRow(pageId, slotIndex);

    /// <inheritdoc />
    protected override void UpdateCore(StorageTransaction transaction, PageId pageId, int slotIndex, ReadOnlySpan<byte> record)
    {
        _storage.UpdateRow(transaction, pageId, slotIndex, record);
        ulong location = SqlRecordLocation.Pack(pageId, slotIndex);
        if (_storage.RowVersionLinks.HasUndoLink(location) && RecordVersionStamp.ReadStamps(record).Deleter == TransactionSequence.None)
        { _storage.RowVersionLinks.Forget(location); }
    }

    /// <inheritdoc />
    protected override void DeleteCore(StorageTransaction transaction, PageId pageId, int slotIndex)
    {
        _storage.DeleteRow(transaction, pageId, slotIndex);
        ulong location = SqlRecordLocation.Pack(pageId, slotIndex);
        if (_storage.RowVersionLinks.HasUndoLink(location)) { _storage.RowVersionLinks.Forget(location); }
    }

    /// <inheritdoc />
    protected override ulong PackLocationCore(PageId pageId, int slotIndex)
        => SqlRecordLocation.Pack(pageId, slotIndex);

    /// <inheritdoc />
    protected override (PageId PageId, int SlotIndex) UnpackLocationCore(ulong location)
        => SqlRecordLocation.Unpack(location);

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlTransactionRecordSpace value)
        {
            _ = value;
        }
    }
}
