using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Blob.Storage.Internal;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Blob.Storage;

/// <summary>Stores blob chunks and catalog records on the shared page, journal, and recovery kernel.</summary>
public sealed class BlobStorage : Assimalign.Cohesion.Database.Storage.Storage
{
    private BlobStorage(StorageStream data, StorageStream journal, StorageStream backup)
        : base(data, journal, backup) => Records = new BlobTransactionRecordSpace(this);

    /// <inheritdoc />
    public override StorageModel Model => StorageModel.Blob;

    /// <summary>Gets the journal used by this storage and its logical transaction coordinator.</summary>
    public IStorageJournal WriteAheadJournal => WriteAheadLog;

    /// <summary>Gets the shared version store's record adapter.</summary>
    public ITransactionRecordSpace Records { get; }

    /// <summary>Creates an empty blob file set.</summary>
    /// <param name="data">The data stream.</param>
    /// <param name="journal">The journal stream.</param>
    /// <param name="backup">The backup stream.</param>
    /// <param name="name">The logical database name.</param>
    /// <returns>The initialized storage.</returns>
    public static BlobStorage Create(StorageStream data, StorageStream journal, StorageStream backup, string name)
        => Create(data, journal, backup, name, null);

    /// <summary>Creates a blob file set with durability resolved before initialization.</summary>
    /// <param name="data">The data stream.</param>
    /// <param name="journal">The journal stream.</param>
    /// <param name="backup">The backup stream.</param>
    /// <param name="name">The logical database name.</param>
    /// <param name="durability">The requested durability, or null to derive it from the backing store.</param>
    /// <returns>The initialized storage.</returns>
    public static BlobStorage Create(StorageStream data, StorageStream journal, StorageStream backup, string name, StorageCommitDurability? durability)
    {
        var storage = new BlobStorage(data, journal, backup);
        storage.ConfigureCommitDurability(durability, $"{nameof(BlobStorage)} ({name})");
        storage.InitializeNew((Name)name);
        return storage;
    }

    /// <summary>Creates an empty blob file set, taking ownership of the streams.</summary>
    /// <param name="data">The data stream.</param>
    /// <param name="journal">The journal stream.</param>
    /// <param name="backup">The backup stream.</param>
    /// <param name="name">The logical database name.</param>
    /// <returns>The initialized storage.</returns>
    public static BlobStorage Create(Stream data, Stream journal, Stream backup, string name)
        => Create(new StorageStream(data), new StorageStream(journal), new StorageStream(backup), name);

    /// <summary>Opens an existing blob file set through shared physical recovery.</summary>
    /// <param name="data">The data stream.</param>
    /// <param name="journal">The journal stream.</param>
    /// <param name="backup">The backup stream.</param>
    /// <param name="checkpointOnOpen">Whether to checkpoint immediately; engines pass false until logical recovery completes.</param>
    /// <returns>The recovered storage.</returns>
    public static BlobStorage Open(StorageStream data, StorageStream journal, StorageStream backup, bool checkpointOnOpen = true)
        => Open(data, journal, backup, checkpointOnOpen, null);

    /// <summary>Opens a blob file set with durability resolved before recovery.</summary>
    /// <param name="data">The data stream.</param>
    /// <param name="journal">The journal stream.</param>
    /// <param name="backup">The backup stream.</param>
    /// <param name="checkpointOnOpen">Whether to checkpoint immediately.</param>
    /// <param name="durability">The requested durability, or null to derive it from the backing store.</param>
    /// <returns>The recovered storage.</returns>
    public static BlobStorage Open(StorageStream data, StorageStream journal, StorageStream backup, bool checkpointOnOpen, StorageCommitDurability? durability)
    {
        var storage = new BlobStorage(data, journal, backup);
        storage.ConfigureCommitDurability(durability, nameof(BlobStorage));
        storage.OpenExisting(checkpointOnOpen);
        return storage;
    }

    /// <summary>Opens an existing blob file set, taking ownership of the streams.</summary>
    /// <param name="data">The data stream.</param>
    /// <param name="journal">The journal stream.</param>
    /// <param name="backup">The backup stream.</param>
    /// <param name="checkpointOnOpen">Whether to checkpoint immediately.</param>
    /// <returns>The recovered storage.</returns>
    public static BlobStorage Open(Stream data, Stream journal, Stream backup, bool checkpointOnOpen = true)
        => Open(new StorageStream(data), new StorageStream(journal), new StorageStream(backup), checkpointOnOpen);

    /// <summary>Inserts a stamped metadata record into the catalog's owner-zero pages.</summary>
    /// <param name="transaction">The physical statement bracket.</param>
    /// <param name="entry">The complete stamped record.</param>
    /// <returns>The record's physical location.</returns>
    public (PageId PageId, int SlotIndex) InsertEntry(IStorageTransaction transaction, ReadOnlySpan<byte> entry)
        => InsertRecord(transaction, entry);

    /// <summary>Reads a record through the kernel's checksum-validated page path.</summary>
    /// <param name="pageId">The record's page.</param>
    /// <param name="slotIndex">The record's slot.</param>
    /// <returns>A copy of the record.</returns>
    public ReadOnlyMemory<byte> ReadEntry(PageId pageId, int slotIndex) => ReadRecord(pageId, slotIndex);

    /// <summary>Replaces a record within a physical statement bracket.</summary>
    /// <param name="transaction">The physical statement bracket.</param>
    /// <param name="pageId">The record's page.</param>
    /// <param name="slotIndex">The record's slot.</param>
    /// <param name="entry">The replacement bytes.</param>
    public void UpdateEntry(IStorageTransaction transaction, PageId pageId, int slotIndex, ReadOnlySpan<byte> entry)
        => UpdateRecord(transaction, pageId, slotIndex, entry);

    /// <summary>Reclaims a record, returning its page to the free map when empty.</summary>
    /// <param name="transaction">The physical statement bracket.</param>
    /// <param name="pageId">The record's page.</param>
    /// <param name="slotIndex">The record's slot.</param>
    public void DeleteEntry(IStorageTransaction transaction, PageId pageId, int slotIndex)
        => DeleteRecord(transaction, pageId, slotIndex);

    /// <summary>Packs a page and slot into a stable, nonzero content location.</summary>
    /// <param name="pageId">The page identifier.</param>
    /// <param name="slotIndex">The slot index.</param>
    /// <returns>The page in the high 48 bits and the slot in the low 16 bits.</returns>
    public static ulong PackLocation(PageId pageId, int slotIndex)
        => ((ulong)(long)pageId << 16) | checked((ushort)slotIndex);

    /// <summary>Decodes a packed content location.</summary>
    /// <param name="location">The packed location.</param>
    /// <returns>The page and slot.</returns>
    public static (PageId PageId, int SlotIndex) UnpackLocation(ulong location)
        => ((PageId)(long)(location >> 16), (int)(location & 0xffff));

    /// <summary>Opens a bounded-memory upload in the caller's logical transaction.</summary>
    /// <param name="coordinator">The database's coordinator.</param>
    /// <param name="context">The logical transaction spanning all chunks and metadata.</param>
    /// <param name="complete">Publishes metadata and, for an automatic transaction, commits after successful disposal.</param>
    /// <param name="abort">Aborts the logical transaction after any upload failure.</param>
    /// <param name="cancellationToken">Cancellation retained for the upload's entire lifetime.</param>
    /// <returns>A non-seekable writable stream holding one chunk buffer.</returns>
    public Stream OpenWrite(TransactionCoordinator coordinator, ITransactionContext context,
        Func<BlobContentReference, ValueTask> complete, Func<ValueTask> abort, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(complete);
        ArgumentNullException.ThrowIfNull(abort);
        cancellationToken.ThrowIfCancellationRequested();
        return new BlobWriteStream(this, coordinator, context, complete, abort, cancellationToken);
    }

    /// <summary>Opens a bounded-memory download over an already-visible content reference.</summary>
    /// <param name="content">The content selected by the caller's snapshot.</param>
    /// <param name="onDispose">Releases the snapshot that pins the chain until the stream closes.</param>
    /// <returns>A non-seekable readable stream.</returns>
    public Stream OpenRead(BlobContentReference content, Func<ValueTask>? onDispose = null)
        => new BlobReadStream(this, content, onDispose);

    /// <summary>Tombstones a chain one chunk per physical bracket, preserving snapshot readers until version purge.</summary>
    /// <param name="coordinator">The database's coordinator.</param>
    /// <param name="context">The logical deleting transaction.</param>
    /// <param name="content">The chain being deleted or replaced.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task representing the tombstone operation.</returns>
    public async ValueTask TombstoneContentAsync(TransactionCoordinator coordinator, ITransactionContext context,
        BlobContentReference content, CancellationToken cancellationToken = default)
    {
        ulong location = content.Head;
        long remaining = content.Length;
        while (location != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (page, slot) = UnpackLocation(location);
            var bytes = ReadEntry(page, slot);
            var chunk = BlobChunkCodec.Decode(bytes, remaining);
            await coordinator.ApplyStatementAsync(context, bracket =>
            {
                UpdateEntry(bracket, page, slot, RecordVersionStamp.WithDeleter(bytes.Span, context.Sequence));
                coordinator.VersionStore.RecordTombstoned(context.Sequence, page, slot);
                return true;
            }, cancellationToken).ConfigureAwait(false);
            remaining -= chunk.Payload.Length;
            location = chunk.Next;
        }
        if (remaining != 0)
        {
            throw new StorageCorruptionException("Blob chain ends before its catalog length.");
        }
    }

    internal (PageId PageId, int SlotIndex) InsertChunk(IStorageTransaction transaction, TransactionSequence writer, ReadOnlySpan<byte> entry)
        => InsertRecord(transaction, writer.Value | (1UL << 63), entry);
}
