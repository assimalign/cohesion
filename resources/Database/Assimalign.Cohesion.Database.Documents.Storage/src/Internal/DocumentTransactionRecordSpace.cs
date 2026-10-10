using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents.Storage.Internal;

internal sealed class DocumentTransactionRecordSpace : TransactionRecordSpace
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

    protected override ReadOnlyMemory<byte> ReadCore(PageId pageId, int slotIndex) => _storage.ReadEntry(pageId, slotIndex);
    protected override void UpdateCore(StorageTransaction transaction, PageId pageId, int slotIndex, ReadOnlySpan<byte> record)
        => _storage.UpdateEntry(transaction, pageId, slotIndex, record);
    protected override void DeleteCore(StorageTransaction transaction, PageId pageId, int slotIndex) => _storage.DeleteEntry(transaction, pageId, slotIndex);
    protected override ulong PackLocationCore(PageId pageId, int slotIndex) => DocumentStorage.PackLocation(pageId, slotIndex);
    protected override (PageId PageId, int SlotIndex) UnpackLocationCore(ulong location) => DocumentStorage.UnpackLocation(location);
}
