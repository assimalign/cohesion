using System;
using System.IO;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Documents.Catalog;
using Assimalign.Cohesion.Database.Documents.Storage;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents.Tests.TestObjects;

internal sealed class CatalogHistoryDatabase : IAsyncDisposable
{
    private readonly FaultingRecordSpace _records;

    internal CatalogHistoryDatabase()
    {
        Storage = DocumentStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "catalog-history");
        _records = new FaultingRecordSpace(Storage.Records);
        Coordinator = new TransactionCoordinator(Storage, Storage.WriteAheadJournal, _records);
        Catalog = DocumentCatalog.Open(Storage, Coordinator);
    }

    internal DocumentStorage Storage { get; }
    internal TransactionCoordinator Coordinator { get; }
    internal DocumentCatalog Catalog { get; set; }
    internal bool FailUndo { get => _records.FailUndo; set => _records.FailUndo = value; }

    internal Exception? UndoFailure { get => _records.UndoFailure; set => _records.UndoFailure = value; }

    public async ValueTask DisposeAsync()
    {
        _records.FailUndo = false;
        await Coordinator.DisposeAsync();
        await Storage.DisposeAsync();
    }

    private sealed class FaultingRecordSpace : TransactionRecordSpace
    {
        private readonly TransactionRecordSpace _inner;

        internal FaultingRecordSpace(TransactionRecordSpace inner) => _inner = inner;

        internal bool FailUndo { get; set; }
        internal Exception? UndoFailure { get; set; }

        protected override ReadOnlyMemory<byte> ReadCore(PageId pageId, int slotIndex)
        {
            if (FailUndo) { throw UndoFailure ?? new IOException("Injected catalog undo failure."); }
            return _inner.Read(pageId, slotIndex);
        }

        protected override void UpdateCore(StorageTransaction transaction, PageId pageId, int slotIndex, ReadOnlySpan<byte> record)
        {
            if (FailUndo) { throw UndoFailure ?? new IOException("Injected catalog undo failure."); }
            _inner.Update(transaction, pageId, slotIndex, record);
        }
        protected override void DeleteCore(StorageTransaction transaction, PageId pageId, int slotIndex)
        {
            if (FailUndo) { throw UndoFailure ?? new IOException("Injected catalog undo failure."); }
            _inner.Delete(transaction, pageId, slotIndex);
        }
        protected override ulong PackLocationCore(PageId pageId, int slotIndex) => _inner.PackLocation(pageId, slotIndex);
        protected override (PageId PageId, int SlotIndex) UnpackLocationCore(ulong location) => _inner.UnpackLocation(location);
    }
}
