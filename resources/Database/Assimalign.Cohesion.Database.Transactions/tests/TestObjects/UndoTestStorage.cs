using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Transactions.Tests.TestObjects;

// Real slotted records and WAL, including index effects backed by separate stamped records.
// This tests the kernel seam without taking a model reference or granting foreign internals.
internal sealed class UndoTestStorage : Storage.Storage
{
    private readonly CrashSimulationStream _data;
    private readonly CrashSimulationStream _journal;

    internal UndoTestStorage(StorageModel model = StorageModel.Custom, (byte[] Data, byte[] Journal)? image = null,
        CrashPoint? point = null, int poolCapacity = 64)
        : this(model, new CrashSimulationStream(image?.Data ?? [], writeThrough: true, point, "data"),
            new CrashSimulationStream(image?.Journal ?? [], writeThrough: true, point, "journal"), image.HasValue, poolCapacity)
    {
    }

    private UndoTestStorage(StorageModel model, CrashSimulationStream data, CrashSimulationStream journal, bool reopen, int poolCapacity)
        : base(model, new StorageStream(data), new StorageStream(journal), StorageStream.FromInMemory(), poolCapacity)
    {
        _data = data;
        _journal = journal;
        Records = new RecordSpace(this);
        if (reopen) { OpenExisting(checkpointOnOpen: false); }
        else { InitializeNew((Name)"undo-substrate"); }
    }

    internal StorageJournal Log => WriteAheadLog;
    internal TransactionRecordSpace Records { get; }
    internal (byte[] Data, byte[] Journal) Capture() => (_data.CaptureDurable(), _journal.CaptureDurable());
    internal void FlushDurable() { PageManager.FlushAll(); Log.Flush(); }
    internal (PageId PageId, int SlotIndex) Insert(StorageTransaction bracket, ReadOnlySpan<byte> record) => InsertRecord(bracket, record);
    internal ReadOnlyMemory<byte> Read(ulong location)
    {
        var (page, slot) = Unpack(location);
        return ReadRecord(page, slot);
    }
    internal void Update(StorageTransaction bracket, ulong location, ReadOnlySpan<byte> record)
    {
        var (page, slot) = Unpack(location);
        UpdateRecord(bracket, page, slot, record);
    }
    internal void Delete(StorageTransaction bracket, ulong location)
    {
        var (page, slot) = Unpack(location);
        DeleteRecord(bracket, page, slot);
    }
    internal static ulong Pack(PageId page, int slot) => ((ulong)(long)page << 16) | (ushort)slot;
    internal static (PageId Page, int Slot) Unpack(ulong location) => ((PageId)(long)(location >> 16), (int)(location & 0xFFFF));

    private sealed class RecordSpace : TransactionRecordSpace
    {
        private readonly UndoTestStorage _storage;
        internal RecordSpace(UndoTestStorage storage) { _storage = storage; }
        protected override ReadOnlyMemory<byte> ReadCore(PageId pageId, int slotIndex) => _storage.Read(Pack(pageId, slotIndex));
        protected override void UpdateCore(StorageTransaction transaction, PageId pageId, int slotIndex, ReadOnlySpan<byte> record)
            => _storage.Update(transaction, Pack(pageId, slotIndex), record);
        protected override void DeleteCore(StorageTransaction transaction, PageId pageId, int slotIndex)
            => _storage.Delete(transaction, Pack(pageId, slotIndex));
        protected override ulong PackLocationCore(PageId pageId, int slotIndex) => Pack(pageId, slotIndex);
        protected override (PageId PageId, int SlotIndex) UnpackLocationCore(ulong location) => Unpack(location);
    }

    internal sealed class Index : RecordVersionIndex
    {
        private readonly UndoTestStorage _storage;
        internal Index(UndoTestStorage storage) { _storage = storage; }
        internal Action? BeforeUndo { get; set; }
        internal int Calls { get; private set; }

        protected override ValueTask EraseCoreAsync(StorageTransaction transaction, ReadOnlyMemory<byte> key, ulong entryReference,
            TransactionSequence writer, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            BeforeUndo?.Invoke();
            var (page, slot) = Unpack(entryReference);
            if (_storage.TryReadRecord(page, slot, out var record) && RecordVersionStamp.ReadStamps(record.Span).Writer == writer)
            { _storage.Delete(transaction, entryReference); }
            return default;
        }

        protected override ValueTask ClearDeleterCoreAsync(StorageTransaction transaction, ReadOnlyMemory<byte> key, ulong entryReference,
            TransactionSequence writer, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            BeforeUndo?.Invoke();
            var (page, slot) = Unpack(entryReference);
            if (_storage.TryReadRecord(page, slot, out var record) && RecordVersionStamp.ReadStamps(record.Span).Deleter == writer)
            { _storage.Update(transaction, entryReference, RecordVersionStamp.WithoutDeleter(record.Span)); }
            return default;
        }
    }
}
