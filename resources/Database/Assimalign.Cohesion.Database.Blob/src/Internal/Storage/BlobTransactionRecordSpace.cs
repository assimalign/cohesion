using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

using Assimalign.Cohesion.Database.Blob.Storage;

namespace Assimalign.Cohesion.Database.Blob.Internal;

[global::System.Diagnostics.DebuggerDisplay("BlobTransactionRecordSpace")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Blob.Internal.BlobTransactionRecordSpace.DebugView))]
internal sealed class BlobTransactionRecordSpace : TransactionRecordSpace
{
    private readonly BlobStorage _storage;

    /// <summary>Initializes a new instance of the <see cref="BlobTransactionRecordSpace"/> class.</summary>
    /// <param name="storage">The blob storage whose records this space reads, updates, and deletes.</param>
    public BlobTransactionRecordSpace(BlobStorage storage)
    {
        _storage = storage;
    }

    internal void RegisterWriteStream(TransactionContext context) => BeginWriteStream(context);
    internal void UnregisterWriteStream(TransactionContext context) => EndWriteStream(context);
    protected override void OnUndoScopesChangedCore(TransactionSequence writer, bool active, RecordSpaceVersionStore ledger) => _storage.UndoScopesChanged?.Invoke(writer, active);
    protected override void OnTransactionEndedCore(TransactionContext context) => _storage.UndoScopesChanged?.Invoke(context.Sequence, false);
    protected override void OnCreatedVersionUndoneCore(PageId page, int slot, ReadOnlyMemory<byte> record) => _storage.CreatedVersionUndone?.Invoke(page, slot, record);

    protected override ReadOnlyMemory<byte> ReadCore(PageId pageId, int slotIndex) => _storage.ReadEntry(pageId, slotIndex);
    protected override void UpdateCore(StorageTransaction transaction, PageId pageId, int slotIndex, ReadOnlySpan<byte> record)
        => _storage.UpdateEntry(transaction, pageId, slotIndex, record);
    protected override void DeleteCore(StorageTransaction transaction, PageId pageId, int slotIndex) => _storage.DeleteEntry(transaction, pageId, slotIndex);
    protected override ulong PackLocationCore(PageId pageId, int slotIndex) => BlobStorage.PackLocation(pageId, slotIndex);
    protected override (PageId PageId, int SlotIndex) UnpackLocationCore(ulong location) => BlobStorage.UnpackLocation(location);

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Blob.Internal.BlobTransactionRecordSpace value)
        {
            _ = value;
        }
    }
}
