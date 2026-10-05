using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Blob.Storage.Internal;

internal sealed class BlobTransactionRecordSpace : TransactionRecordSpace
{
    private readonly BlobStorage _storage;

    /// <summary>Initializes a new instance of the <see cref="BlobTransactionRecordSpace"/> class.</summary>
    /// <param name="storage">The blob storage whose records this space reads, updates, and deletes.</param>
    public BlobTransactionRecordSpace(BlobStorage storage)
    {
        _storage = storage;
    }

    protected override ReadOnlyMemory<byte> ReadCore(PageId pageId, int slotIndex) => _storage.ReadEntry(pageId, slotIndex);
    protected override void UpdateCore(StorageTransaction transaction, PageId pageId, int slotIndex, ReadOnlySpan<byte> record)
        => _storage.UpdateEntry(transaction, pageId, slotIndex, record);
    protected override void DeleteCore(StorageTransaction transaction, PageId pageId, int slotIndex) => _storage.DeleteEntry(transaction, pageId, slotIndex);
    protected override ulong PackLocationCore(PageId pageId, int slotIndex) => BlobStorage.PackLocation(pageId, slotIndex);
    protected override (PageId PageId, int SlotIndex) UnpackLocationCore(ulong location) => BlobStorage.UnpackLocation(location);
}
