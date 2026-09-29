using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents.Storage.Internal;

internal sealed class DocumentTransactionRecordSpace : ITransactionRecordSpace
{
    private readonly DocumentStorage _storage;

    /// <summary>
    /// Initializes a new instance of the <see cref="DocumentTransactionRecordSpace"/> class.
    /// </summary>
    /// <param name="storage">The document storage whose records the version store reads and rewrites.</param>
    public DocumentTransactionRecordSpace(DocumentStorage storage)
    {
        _storage = storage;
    }

    public ReadOnlyMemory<byte> Read(PageId pageId, int slotIndex) => _storage.ReadEntry(pageId, slotIndex);
    public void Update(IStorageTransaction transaction, PageId pageId, int slotIndex, ReadOnlySpan<byte> record)
        => _storage.UpdateEntry(transaction, pageId, slotIndex, record);
    public void Delete(IStorageTransaction transaction, PageId pageId, int slotIndex) => _storage.DeleteEntry(transaction, pageId, slotIndex);
    public ulong PackLocation(PageId pageId, int slotIndex) => DocumentStorage.PackLocation(pageId, slotIndex);
    public (PageId PageId, int SlotIndex) UnpackLocation(ulong location) => DocumentStorage.UnpackLocation(location);
}
