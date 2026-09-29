using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Blob.Storage.Internal;

internal sealed class BlobTransactionRecordSpace : ITransactionRecordSpace
{
    private readonly BlobStorage _storage;

    /// <summary>Initializes a new instance of the <see cref="BlobTransactionRecordSpace"/> class.</summary>
    /// <param name="storage">The blob storage whose records this space reads, updates, and deletes.</param>
    public BlobTransactionRecordSpace(BlobStorage storage)
    {
        _storage = storage;
    }

    public ReadOnlyMemory<byte> Read(PageId pageId, int slotIndex) => _storage.ReadEntry(pageId, slotIndex);
    public void Update(IStorageTransaction transaction, PageId pageId, int slotIndex, ReadOnlySpan<byte> record)
        => _storage.UpdateEntry(transaction, pageId, slotIndex, record);
    public void Delete(IStorageTransaction transaction, PageId pageId, int slotIndex) => _storage.DeleteEntry(transaction, pageId, slotIndex);
    public ulong PackLocation(PageId pageId, int slotIndex) => BlobStorage.PackLocation(pageId, slotIndex);
    public (PageId PageId, int SlotIndex) UnpackLocation(ulong location) => BlobStorage.UnpackLocation(location);
}
