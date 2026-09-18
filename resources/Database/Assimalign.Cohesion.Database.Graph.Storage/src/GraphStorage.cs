using System;
using System.IO;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Storage;

/// <summary>Owns graph pages and delegates journaling, allocation, and recovery to the shared kernel.</summary>
public sealed class GraphStorage : Assimalign.Cohesion.Database.Storage.Storage
{
    private GraphStorage(StorageStream data, StorageStream journal, StorageStream backup) : base(data, journal, backup)
        => Records = new RecordSpace(this);
    /// <inheritdoc />
    public override StorageModel Model => StorageModel.Graph;
    /// <summary>Gets this file set's write-ahead journal.</summary>
    public IStorageJournal WriteAheadJournal => WriteAheadLog;
    /// <summary>Gets the stamped-record adapter for the transaction coordinator.</summary>
    public ITransactionRecordSpace Records { get; }
    /// <summary>Creates a graph file set, taking ownership of its streams.</summary>
    /// <param name="data">Data stream.</param><param name="journal">Journal stream.</param><param name="backup">Backup stream.</param><param name="name">Database name.</param>
    /// <returns>The initialized storage.</returns>
    public static GraphStorage Create(StorageStream data, StorageStream journal, StorageStream backup, string name)
        => Create(data, journal, backup, name, null);

    /// <summary>Creates a graph file set with durability resolved before initialization.</summary>
    /// <param name="data">The data stream.</param>
    /// <param name="journal">The journal stream.</param>
    /// <param name="backup">The backup stream.</param>
    /// <param name="name">The logical database name.</param>
    /// <param name="durability">The requested durability, or null to derive it from the backing store.</param>
    /// <returns>The initialized storage.</returns>
    public static GraphStorage Create(StorageStream data, StorageStream journal, StorageStream backup, string name, StorageCommitDurability? durability)
    {
        var result = new GraphStorage(data, journal, backup);
        result.ConfigureCommitDurability(durability, $"{nameof(GraphStorage)} ({name})");
        result.InitializeNew((Name)name);
        return result;
    }
    /// <summary>Creates a graph file set, taking ownership of its streams.</summary>
    /// <param name="data">Data stream.</param><param name="journal">Journal stream.</param><param name="backup">Backup stream.</param><param name="name">Database name.</param>
    /// <returns>The initialized storage.</returns>
    public static GraphStorage Create(Stream data, Stream journal, Stream backup, string name)
        => Create(new StorageStream(data), new StorageStream(journal), new StorageStream(backup), name);
    /// <summary>Opens graph pages through physical recovery; engines defer checkpoint until logical recovery completes.</summary>
    /// <param name="data">Data stream.</param><param name="journal">Journal stream.</param><param name="backup">Backup stream.</param><param name="checkpointOnOpen">Whether to checkpoint immediately.</param>
    /// <returns>The recovered storage.</returns>
    public static GraphStorage Open(StorageStream data, StorageStream journal, StorageStream backup, bool checkpointOnOpen = false)
        => Open(data, journal, backup, checkpointOnOpen, null);

    /// <summary>Opens a graph file set with durability resolved before recovery.</summary>
    /// <param name="data">The data stream.</param>
    /// <param name="journal">The journal stream.</param>
    /// <param name="backup">The backup stream.</param>
    /// <param name="checkpointOnOpen">Whether to checkpoint immediately.</param>
    /// <param name="durability">The requested durability, or null to derive it from the backing store.</param>
    /// <returns>The recovered storage.</returns>
    public static GraphStorage Open(StorageStream data, StorageStream journal, StorageStream backup, bool checkpointOnOpen, StorageCommitDurability? durability)
    {
        var result = new GraphStorage(data, journal, backup);
        result.ConfigureCommitDurability(durability, nameof(GraphStorage));
        result.OpenExisting(checkpointOnOpen);
        return result;
    }
    /// <summary>Opens a graph file set, taking ownership of its streams.</summary>
    /// <param name="data">Data stream.</param><param name="journal">Journal stream.</param><param name="backup">Backup stream.</param><param name="checkpointOnOpen">Whether to checkpoint immediately.</param>
    /// <returns>The recovered storage.</returns>
    public static GraphStorage Open(Stream data, Stream journal, Stream backup, bool checkpointOnOpen = false)
        => Open(new StorageStream(data), new StorageStream(journal), new StorageStream(backup), checkpointOnOpen);
    /// <summary>Inserts a stamped catalog record on owner-zero pages.</summary>
    /// <param name="transaction">Physical statement bracket.</param><param name="entry">Stamped record.</param><returns>The record location.</returns>
    public (PageId PageId, int SlotIndex) InsertEntry(IStorageTransaction transaction, ReadOnlySpan<byte> entry) => InsertRecord(transaction, entry);
    /// <summary>Reads a checksum-validated record.</summary>
    /// <param name="pageId">Page identity.</param><param name="slotIndex">Slot number.</param><returns>The record bytes.</returns>
    public ReadOnlyMemory<byte> ReadEntry(PageId pageId, int slotIndex) => ReadRecord(pageId, slotIndex);
    /// <summary>Replaces a record within a statement bracket.</summary>
    /// <param name="transaction">Physical statement bracket.</param><param name="pageId">Page identity.</param><param name="slotIndex">Slot number.</param><param name="entry">Replacement record.</param>
    public void UpdateEntry(IStorageTransaction transaction, PageId pageId, int slotIndex, ReadOnlySpan<byte> entry) => UpdateRecord(transaction, pageId, slotIndex, entry);
    /// <summary>Reclaims a record within a statement bracket.</summary>
    /// <param name="transaction">Physical statement bracket.</param><param name="pageId">Page identity.</param><param name="slotIndex">Slot number.</param>
    public void DeleteEntry(IStorageTransaction transaction, PageId pageId, int slotIndex) => DeleteRecord(transaction, pageId, slotIndex);
    /// <summary>Packs a page and slot into a stable physical reference.</summary>
    /// <param name="pageId">Page identity.</param><param name="slotIndex">Slot number.</param><returns>The packed location.</returns>
    public static ulong PackLocation(PageId pageId, int slotIndex) => ((ulong)(long)pageId << 16) | checked((ushort)slotIndex);
    /// <summary>Decodes a packed physical reference.</summary>
    /// <param name="location">Packed location.</param><returns>The page and slot.</returns>
    public static (PageId PageId, int SlotIndex) UnpackLocation(ulong location) => ((PageId)(long)(location >> 16), (int)(location & 0xffff));
    internal (PageId PageId, int SlotIndex) InsertOwned(IStorageTransaction transaction, ulong owner, ReadOnlySpan<byte> bytes) => InsertRecord(transaction, owner, bytes);
    private sealed class RecordSpace(GraphStorage storage) : ITransactionRecordSpace
    {
        public ReadOnlyMemory<byte> Read(PageId pageId, int slotIndex) => storage.ReadEntry(pageId, slotIndex);
        public void Update(IStorageTransaction transaction, PageId pageId, int slotIndex, ReadOnlySpan<byte> record) => storage.UpdateEntry(transaction, pageId, slotIndex, record);
        public void Delete(IStorageTransaction transaction, PageId pageId, int slotIndex) => storage.DeleteEntry(transaction, pageId, slotIndex);
        public ulong PackLocation(PageId pageId, int slotIndex) => GraphStorage.PackLocation(pageId, slotIndex);
        public (PageId PageId, int SlotIndex) UnpackLocation(ulong location) => GraphStorage.UnpackLocation(location);
    }
}
