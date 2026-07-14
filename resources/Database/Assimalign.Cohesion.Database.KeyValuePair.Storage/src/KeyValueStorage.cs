using System;

namespace Assimalign.Cohesion.Database.KeyValuePair.Storage;

using Assimalign.Cohesion.Database.Storage;

/// <summary>
/// The key-value model's storage binding: an entry-record space on the shared
/// page/WAL substrate. Entries are stored as variable-length byte records in
/// slotted pages; the key-value engine keys the record chain by key-space owner
/// id so a key space's records get per-object page locality.
/// </summary>
/// <remarks>
/// Each <see cref="KeyValueStorage"/> instance manages three file assets: data
/// (<c>.dat</c>), journal (<c>.log</c>), and backup (<c>.bak</c>). Use the static
/// factory methods <see cref="Create(StorageStream, StorageStream, StorageStream, string)"/>
/// and <see cref="Open(StorageStream, StorageStream, StorageStream, bool)"/> to
/// create or open storage instances. The class deliberately exposes only the
/// record surface the key-value engine composes — the physical layout (pages,
/// buffer pool, journal) is the shared kernel's, unchanged.
/// </remarks>
public sealed class KeyValueStorage : Assimalign.Cohesion.Database.Storage.Storage
{
    private KeyValueStorage(StorageStream data, StorageStream journal, StorageStream backup)
        : base(data, journal, backup) { }

    /// <inheritdoc />
    public override StorageModel Model => StorageModel.KeyValue;

    /// <summary>
    /// Creates a new key-value storage file set backed by the given streams.
    /// </summary>
    /// <param name="data">The storage stream for the data file (<c>.dat</c>).</param>
    /// <param name="journal">The storage stream for the journal file (<c>.log</c>).</param>
    /// <param name="backup">The storage stream for the backup file (<c>.bak</c>).</param>
    /// <param name="name">A name for this storage instance (e.g., database name).</param>
    /// <returns>A new <see cref="KeyValueStorage"/> ready for use.</returns>
    public static KeyValueStorage Create(StorageStream data, StorageStream journal, StorageStream backup, string name)
    {
        var storage = new KeyValueStorage(data, journal, backup);
        storage.InitializeNew((Name)name);
        return storage;
    }

    /// <summary>
    /// Creates a new key-value storage file set backed by arbitrary streams.
    /// </summary>
    /// <param name="data">The data stream (<c>.dat</c>).</param>
    /// <param name="journal">The journal stream (<c>.log</c>).</param>
    /// <param name="backup">The backup stream (<c>.bak</c>).</param>
    /// <param name="name">A name for this storage instance (e.g., database name).</param>
    /// <returns>A new <see cref="KeyValueStorage"/> ready for use.</returns>
    public static KeyValueStorage Create(System.IO.Stream data, System.IO.Stream journal, System.IO.Stream backup, string name)
    {
        return Create(new StorageStream(data), new StorageStream(journal), new StorageStream(backup), name);
    }

    /// <summary>
    /// Opens an existing key-value storage file set from the given streams.
    /// </summary>
    /// <param name="data">The storage stream for the data file (<c>.dat</c>).</param>
    /// <param name="journal">The storage stream for the journal file (<c>.log</c>).</param>
    /// <param name="backup">The storage stream for the backup file (<c>.bak</c>).</param>
    /// <param name="checkpointOnOpen">
    /// When true (the default), a journal with records is checkpointed (truncated)
    /// after recovery. The key-value engine passes false so it can run
    /// transaction-recovery analysis over the recovered journal — classification
    /// reads lifecycle records the truncation would destroy — and checkpoints
    /// itself afterwards.
    /// </param>
    /// <returns>A <see cref="KeyValueStorage"/> loaded from the streams.</returns>
    public static KeyValueStorage Open(StorageStream data, StorageStream journal, StorageStream backup, bool checkpointOnOpen = true)
    {
        var storage = new KeyValueStorage(data, journal, backup);
        storage.OpenExisting(checkpointOnOpen);
        return storage;
    }

    /// <summary>
    /// Opens an existing key-value storage file set from arbitrary streams.
    /// </summary>
    /// <param name="data">The data stream (<c>.dat</c>).</param>
    /// <param name="journal">The journal stream (<c>.log</c>).</param>
    /// <param name="backup">The backup stream (<c>.bak</c>).</param>
    /// <param name="checkpointOnOpen">When true (the default), a journal with records is checkpointed after recovery; see the primary overload.</param>
    /// <returns>A <see cref="KeyValueStorage"/> loaded from the streams.</returns>
    public static KeyValueStorage Open(System.IO.Stream data, System.IO.Stream journal, System.IO.Stream backup, bool checkpointOnOpen = true)
    {
        return Open(new StorageStream(data), new StorageStream(journal), new StorageStream(backup), checkpointOnOpen);
    }

    /// <summary>
    /// Gets the write-ahead journal, for the engine's transaction coordinator: the
    /// manager's journal-bound transaction log and open-time recovery analysis
    /// (<c>TransactionRecovery.Analyze</c>) both ride the same journal the storage
    /// brackets write page images to.
    /// </summary>
    internal IStorageJournal WriteAheadJournal => WriteAheadLog;

    /// <summary>
    /// Inserts an entry record into the specified key space's record chain within
    /// a storage transaction. The key-value engine passes its key-space owner id,
    /// giving the key space per-object page locality.
    /// </summary>
    /// <param name="transaction">The owning storage transaction.</param>
    /// <param name="ownerId">The owning key space's object id; zero is the shared space.</param>
    /// <param name="entry">The serialized entry bytes.</param>
    /// <returns>The page and slot location where the entry was written.</returns>
    public (PageId PageId, int SlotIndex) InsertEntry(IStorageTransaction transaction, ulong ownerId, ReadOnlySpan<byte> entry)
    {
        return InsertRecord(transaction, ownerId, entry);
    }

    /// <summary>
    /// Inserts an entry record within a storage transaction, into the shared
    /// (owner-zero) space — the catalog file set's record path.
    /// </summary>
    /// <param name="transaction">The owning storage transaction.</param>
    /// <param name="entry">The serialized entry bytes.</param>
    /// <returns>The page and slot location where the entry was written.</returns>
    public (PageId PageId, int SlotIndex) InsertEntry(IStorageTransaction transaction, ReadOnlySpan<byte> entry)
    {
        return InsertRecord(transaction, entry);
    }

    /// <summary>
    /// Reads an entry record from the specified page and slot.
    /// </summary>
    /// <param name="pageId">The page containing the entry.</param>
    /// <param name="slotIndex">The slot index within the page.</param>
    /// <returns>A copy of the entry bytes.</returns>
    public ReadOnlyMemory<byte> ReadEntry(PageId pageId, int slotIndex)
    {
        return ReadRecord(pageId, slotIndex);
    }

    /// <summary>
    /// Updates an entry record at the specified page and slot within a storage
    /// transaction (the same-length tombstone-stamp write path).
    /// </summary>
    /// <param name="transaction">The owning storage transaction.</param>
    /// <param name="pageId">The page containing the entry.</param>
    /// <param name="slotIndex">The slot index within the page.</param>
    /// <param name="entry">The new entry bytes.</param>
    public void UpdateEntry(IStorageTransaction transaction, PageId pageId, int slotIndex, ReadOnlySpan<byte> entry)
    {
        UpdateRecord(transaction, pageId, slotIndex, entry);
    }

    /// <summary>
    /// Deletes an entry record at the specified page and slot within a storage
    /// transaction (version reclamation and recovery scrub).
    /// </summary>
    /// <param name="transaction">The owning storage transaction.</param>
    /// <param name="pageId">The page containing the entry.</param>
    /// <param name="slotIndex">The slot index within the page.</param>
    public void DeleteEntry(IStorageTransaction transaction, PageId pageId, int slotIndex)
    {
        DeleteRecord(transaction, pageId, slotIndex);
    }
}
