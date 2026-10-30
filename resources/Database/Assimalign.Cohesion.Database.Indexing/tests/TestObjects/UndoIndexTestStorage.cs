using System;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Tests;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Indexing.Tests.TestObjects;

internal sealed class UndoIndexTestStorage : Storage.Storage
{
    internal UndoIndexTestStorage()
        : base(StorageModel.Custom, new StorageStream(new SimulatedDurableFileHandle()),
            new StorageStream(new SimulatedDurableFileHandle()), StorageStream.FromInMemory())
    {
        InitializeNew((Name)"index-undo");
        Records = new RecordSpace(this);
    }

    internal StorageJournal Log => WriteAheadLog;
    internal TransactionRecordSpace Records { get; }
    internal (PageId PageId, int SlotIndex) Insert(StorageTransaction bracket, byte[] record) => InsertRecord(bracket, record);

    private sealed class RecordSpace : TransactionRecordSpace
    {
        private readonly UndoIndexTestStorage _storage;
        internal RecordSpace(UndoIndexTestStorage storage) { _storage = storage; }
        protected override ReadOnlyMemory<byte> ReadCore(PageId pageId, int slotIndex) => _storage.ReadRecord(pageId, slotIndex);
        protected override void UpdateCore(StorageTransaction transaction, PageId pageId, int slotIndex, ReadOnlySpan<byte> record)
            => _storage.UpdateRecord(transaction, pageId, slotIndex, record);
        protected override void DeleteCore(StorageTransaction transaction, PageId pageId, int slotIndex)
            => _storage.DeleteRecord(transaction, pageId, slotIndex);
        protected override ulong PackLocationCore(PageId pageId, int slotIndex) => ((ulong)(long)pageId << 16) | (ushort)slotIndex;
        protected override (PageId PageId, int SlotIndex) UnpackLocationCore(ulong location)
            => ((PageId)(long)(location >> 16), (int)(location & 0xFFFF));
    }
}
