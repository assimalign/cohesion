using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Storage;

using Assimalign.Cohesion.Database.Storage.Internal;
using Assimalign.Cohesion.Database.Storage.Units;

/// <summary>
/// Abstract base class for all storage implementations. Manages three file assets
/// (data, journal, backup), page allocation, buffer caching, record-level I/O
/// through slotted pages, and storage-level transactions over the write-ahead log.
/// </summary>
/// <remarks>
/// <para>
/// Derived classes (SQL, Document, Graph, KeyValue) provide model-specific APIs that
/// delegate to the record operations defined here. All models share the same
/// page-based storage infrastructure operating on the <see cref="Data"/> stream, with
/// a per-database write-ahead log backed by the <see cref="Journal"/> stream.
/// </para>
/// <para>
/// <b>Durability model (steal / no-force).</b> Record mutations run inside an
/// <see cref="IStorageTransaction"/>: the first touch of a page journals its before
/// image, commit journals after images plus a commit record and returns once the
/// journal is durable. Data pages flush lazily — the buffer pool may steal (evict)
/// dirty pages early because the write-ahead gate guarantees the journal covers
/// them, and commit never forces data pages. Opening a storage file replays the
/// journal: committed work is redone, uncommitted work is undone.
/// </para>
/// <para>
/// The file-header page (page 0) is deliberately unlogged: it carries recomputable
/// bookkeeping only, and every field in it is reconstructed or revalidated on open.
/// </para>
/// </remarks>
public abstract class Storage : IStorage
{
    private readonly StorageBufferPool _bufferPool;
    private readonly StorageFreeSpaceMap _freeSpaceMap;
    private readonly Dictionary<long, long> _pageWriteLocks = new();
    private readonly object _transactionLock = new();
    private StoragePageManager? _pageManager;
    private StreamJournal? _journal;
    private long _nextTransactionSequence;
    private StorageId _id;
    private Name _name;
    private PageId? _currentWritePageId;
    private bool _disposed;

    /// <summary>
    /// Initializes the storage with the specified backing streams for data, journal, and backup.
    /// </summary>
    /// <param name="data">The data stream providing page-level I/O for the <c>.dat</c> file.</param>
    /// <param name="journal">The journal stream for the <c>.log</c> file (write-ahead log).</param>
    /// <param name="backup">The backup stream for the <c>.bak</c> file.</param>
    /// <param name="bufferPoolCapacity">Maximum number of pages to cache in memory.</param>
    protected Storage(StorageStream data, StorageStream journal, StorageStream backup, int bufferPoolCapacity = 128)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(backup);

        Data = data;
        Journal = journal;
        Backup = backup;
        _bufferPool = new StorageBufferPool(bufferPoolCapacity);
        _freeSpaceMap = new StorageFreeSpaceMap();
    }

    /// <inheritdoc />
    public StorageId Id => _id;

    /// <inheritdoc />
    public Name Name => _name;

    /// <inheritdoc />
    public abstract StorageModel Model { get; }

    /// <inheritdoc />
    public StorageStream Data { get; }

    /// <inheritdoc />
    public StorageStream Journal { get; }

    /// <inheritdoc />
    public StorageStream Backup { get; }

    /// <inheritdoc />
    public IStoragePageManager PageManager =>
        _pageManager ?? throw new InvalidOperationException("Storage has not been initialized.");

    /// <inheritdoc />
    public IStorageBufferPool BufferPool =>
        _bufferPool ?? throw new InvalidOperationException("Storage has not been initialized.");

    /// <inheritdoc />
    public IStorageFreeSpaceMap FreeSpaceMap =>
        _freeSpaceMap ?? throw new InvalidOperationException("Storage has not been initialized.");

    /// <summary>
    /// Gets the write-ahead log for this storage instance.
    /// </summary>
    /// <remarks>
    /// Available to derived classes and the transaction layer for logical operation
    /// records. Physical durability (page images, commit records, recovery) is
    /// managed by the storage transaction scope — derived classes should not append
    /// page images directly.
    /// </remarks>
    protected IJournal WriteAheadLog =>
        _journal ?? throw new InvalidOperationException("Storage has not been initialized.");

    /// <summary>
    /// Creates a new storage file set with the specified name, writing the file header
    /// and allocating the first data page.
    /// </summary>
    /// <param name="name">The name for this storage instance.</param>
    protected unsafe void InitializeNew(Name name)
    {
        _name = name;
        _id = StorageId.NewId();
        _pageManager = new StoragePageManager(Data, _bufferPool, _freeSpaceMap);
        _journal = new StreamJournal(Journal, leaveOpen: true);
        _bufferPool.WriteAheadGate = lsn => _journal.EnsureDurable(lsn);

        // Allocate and write file header (page 0). The file metadata lives in the
        // page body so the page header (id, LSN, checksum) stays intact.
        using (var headerHandle = _pageManager.AllocatePage(PageType.FileHeader))
        {
            WriteFileHeader(headerHandle);
            headerHandle.MarkDirty();
        }

        // Allocate first data page (page 1)
        var dataHandle = _pageManager.AllocatePage(PageType.Data);
        var slotted = new SlottedPage(dataHandle.Page);
        slotted.Initialize();
        dataHandle.MarkDirty();
        _currentWritePageId = dataHandle.Id;
        dataHandle.Dispose();

        _pageManager.FlushAll();
    }

    /// <summary>
    /// Opens an existing storage file set: validates the file header, replays the
    /// write-ahead log (redo committed work, undo uncommitted work), reconstructs the
    /// free-space map from page headers, and checkpoints so the journal starts clean.
    /// </summary>
    /// <exception cref="StorageCorruptionException">The file header page fails checksum verification.</exception>
    /// <exception cref="StorageIOException">The file header is not a valid Cohesion storage header.</exception>
    protected unsafe void OpenExisting()
    {
        var headerBuffer = new byte[Page.Size];
        Data.ReadPage((PageId)0L, headerBuffer);
        PageChecksum.Verify(headerBuffer, (PageId)0L);

        fixed (byte* ptr = headerBuffer)
        {
            var header = (StorageFileHeader*)(ptr + Page.HeaderSize);

            if (!header->IsValid())
            {
                throw new StorageIOException("Invalid storage file: header magic number mismatch.");
            }

            var idBytes = new byte[16];
            for (int i = 0; i < 16; i++)
            {
                idBytes[i] = header->StorageId[i];
            }
            _id = (StorageId)new Guid(idBytes);

            var nameBytes = new byte[128];
            int nameLen = 0;
            for (int i = 0; i < 128; i++)
            {
                if (header->Name[i] == 0)
                {
                    break;
                }
                nameBytes[i] = header->Name[i];
                nameLen++;
            }
            _name = nameLen > 0
                ? (Name)Encoding.UTF8.GetString(nameBytes, 0, nameLen)
                : (Name)"";
        }

        // Recover before anything reads pages: redo committed changes that never
        // reached the data file, undo stolen uncommitted writes that did.
        _journal = new StreamJournal(Journal, leaveOpen: true);
        _bufferPool.WriteAheadGate = lsn => _journal.EnsureDurable(lsn);
        bool journalHadRecords = _journal.LastLsn > 0;
        _nextTransactionSequence = StorageRecovery.Run(Data, _journal);

        // Rebuild the free-space map and locate the last data page in one pass over
        // the on-disk page headers. The stream length is the source of truth for the
        // page count (the file header trails it if the process stopped between an
        // allocation and the next header update).
        _freeSpaceMap.MarkAllocated((PageId)0L);

        long pageCount = Data.Length / Page.Size;
        var pageHeader = new byte[Page.HeaderSize];
        PageId? lastDataPage = null;

        for (long i = 1; i < pageCount; i++)
        {
            Data.ReadPageHeader((PageId)i, pageHeader);

            PageType type;
            fixed (byte* headerPtr = pageHeader)
            {
                type = ((Page.Header*)headerPtr)->Type;
            }

            if (type == PageType.Free)
            {
                _freeSpaceMap.MarkFree((PageId)i);
            }
            else
            {
                _freeSpaceMap.MarkAllocated((PageId)i);

                if (type == PageType.Data)
                {
                    lastDataPage = (PageId)i;
                }
            }
        }

        _pageManager = new StoragePageManager(Data, _bufferPool, _freeSpaceMap);
        _currentWritePageId = lastDataPage;

        // Everything the journal described is now in the data file; start it clean.
        if (journalHadRecords)
        {
            Checkpoint();
        }
    }

    /// <inheritdoc />
    public IStorageTransaction BeginTransaction()
    {
        if (_journal is null)
        {
            throw new InvalidOperationException("Storage has not been initialized.");
        }

        long sequence;
        lock (_transactionLock)
        {
            sequence = ++_nextTransactionSequence;
        }

        _journal.AppendBegin(sequence);
        return new StorageTransaction(this, sequence);
    }

    /// <inheritdoc />
    public IStoragePageHandle OpenPageForWrite(IStorageTransaction transaction, PageId pageId)
    {
        var owner = ValidateTransaction(transaction);
        return TouchPage(owner, pageId);
    }

    /// <inheritdoc />
    public IStoragePageHandle AllocatePageForWrite(IStorageTransaction transaction, PageType type)
    {
        var owner = ValidateTransaction(transaction);
        var handle = _pageManager!.AllocatePage(type);

        try
        {
            RegisterTouch(owner, handle);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public void Checkpoint()
    {
        lock (_transactionLock)
        {
            if (_pageWriteLocks.Count > 0)
            {
                throw new StorageTransactionException("Checkpoint requires no active transactions.");
            }
        }

        UpdateFileHeader();
        _pageManager?.FlushAll();
        Data.FlushDurable();
        _journal?.Checkpoint(ReadOnlySpan<long>.Empty);
    }

    /// <summary>
    /// Inserts a record within a storage transaction. Automatically allocates a new
    /// data page if the current page is full.
    /// </summary>
    /// <param name="transaction">The owning storage transaction.</param>
    /// <param name="data">The record data to insert.</param>
    /// <returns>The page identifier and slot index where the record was stored.</returns>
    /// <exception cref="SlottedPageException">The record is larger than a single page can hold.</exception>
    /// <exception cref="StorageTransactionException">The transaction is not active, or the target page is owned by another transaction.</exception>
    protected unsafe (PageId PageId, int SlotIndex) InsertRecord(IStorageTransaction transaction, ReadOnlySpan<byte> data)
    {
        var owner = ValidateTransaction(transaction);

        if (data.Length > SlottedPage.MaxRecordSize)
        {
            throw new SlottedPageException(
                $"Record of {data.Length} bytes exceeds the maximum record size of {SlottedPage.MaxRecordSize} bytes.");
        }

        IStoragePageHandle handle;
        SlottedPage slotted;

        // The current write page may have been freed since the last insert.
        if (_currentWritePageId is null || !_freeSpaceMap.IsAllocated(_currentWritePageId.Value))
        {
            handle = AllocateDataPage(owner, out slotted);
        }
        else
        {
            handle = TouchPage(owner, _currentWritePageId.Value);
            slotted = new SlottedPage(handle.Page);

            if (!slotted.CanFit(data.Length))
            {
                handle.Dispose();
                handle = AllocateDataPage(owner, out slotted);
            }
        }

        int slotIndex = slotted.InsertSlot(data);
        handle.MarkDirty();

        var pageId = handle.Id;
        handle.Dispose();

        return (pageId, slotIndex);
    }

    /// <summary>
    /// Inserts a record with auto-commit semantics: a single-operation transaction
    /// that commits (durably) before returning.
    /// </summary>
    /// <param name="data">The record data to insert.</param>
    /// <returns>The page identifier and slot index where the record was stored.</returns>
    protected (PageId PageId, int SlotIndex) InsertRecord(ReadOnlySpan<byte> data)
    {
        using var transaction = BeginTransaction();
        var location = InsertRecord(transaction, data);
        transaction.Commit();
        return location;
    }

    /// <summary>
    /// Inserts a tuple into storage by serializing it to record bytes.
    /// </summary>
    /// <param name="tuple">The tuple to insert.</param>
    /// <returns>The page identifier and slot index where the tuple was stored.</returns>
    protected (PageId PageId, int SlotIndex) InsertTuple(in StorageTuple tuple)
    {
        return InsertRecord(tuple.ToBytes());
    }

    /// <summary>
    /// Reads a record from the specified page and slot.
    /// </summary>
    /// <param name="pageId">The page containing the record.</param>
    /// <param name="slotIndex">The slot index within the page.</param>
    /// <returns>A copy of the record data.</returns>
    protected unsafe ReadOnlyMemory<byte> ReadRecord(PageId pageId, int slotIndex)
    {
        using var handle = _pageManager!.GetPage(pageId);
        var slotted = new SlottedPage(handle.Page);
        int length = slotted.GetSlotLength(slotIndex);
        var buffer = new byte[length];
        slotted.ReadSlot(slotIndex, buffer);
        return buffer;
    }

    /// <summary>
    /// Reads and deserializes a tuple from the specified page and slot.
    /// </summary>
    /// <param name="pageId">The page containing the tuple.</param>
    /// <param name="slotIndex">The slot index within the page.</param>
    /// <returns>The deserialized tuple.</returns>
    protected StorageTuple ReadTuple(PageId pageId, int slotIndex)
    {
        var data = ReadRecord(pageId, slotIndex);
        return StorageTuple.FromBytes(data.Span);
    }

    /// <summary>
    /// Deletes a record within a storage transaction by marking the slot as deleted.
    /// </summary>
    /// <param name="transaction">The owning storage transaction.</param>
    /// <param name="pageId">The page containing the record.</param>
    /// <param name="slotIndex">The slot index within the page.</param>
    /// <exception cref="StorageTransactionException">The transaction is not active, or the target page is owned by another transaction.</exception>
    protected unsafe void DeleteRecord(IStorageTransaction transaction, PageId pageId, int slotIndex)
    {
        var owner = ValidateTransaction(transaction);

        using var handle = TouchPage(owner, pageId);
        var slotted = new SlottedPage(handle.Page);
        slotted.DeleteSlot(slotIndex);
        handle.MarkDirty();
    }

    /// <summary>
    /// Deletes a record with auto-commit semantics.
    /// </summary>
    /// <param name="pageId">The page containing the record.</param>
    /// <param name="slotIndex">The slot index within the page.</param>
    protected void DeleteRecord(PageId pageId, int slotIndex)
    {
        using var transaction = BeginTransaction();
        DeleteRecord(transaction, pageId, slotIndex);
        transaction.Commit();
    }

    /// <summary>
    /// Updates a record within a storage transaction with new data.
    /// </summary>
    /// <param name="transaction">The owning storage transaction.</param>
    /// <param name="pageId">The page containing the record.</param>
    /// <param name="slotIndex">The slot index within the page.</param>
    /// <param name="data">The new record data.</param>
    /// <exception cref="StorageTransactionException">The transaction is not active, or the target page is owned by another transaction.</exception>
    protected unsafe void UpdateRecord(IStorageTransaction transaction, PageId pageId, int slotIndex, ReadOnlySpan<byte> data)
    {
        var owner = ValidateTransaction(transaction);

        using var handle = TouchPage(owner, pageId);
        var slotted = new SlottedPage(handle.Page);
        slotted.UpdateSlot(slotIndex, data);
        handle.MarkDirty();
    }

    /// <summary>
    /// Updates a record with auto-commit semantics.
    /// </summary>
    /// <param name="pageId">The page containing the record.</param>
    /// <param name="slotIndex">The slot index within the page.</param>
    /// <param name="data">The new record data.</param>
    protected void UpdateRecord(PageId pageId, int slotIndex, ReadOnlySpan<byte> data)
    {
        using var transaction = BeginTransaction();
        UpdateRecord(transaction, pageId, slotIndex, data);
        transaction.Commit();
    }

    /// <summary>
    /// Updates a tuple at the specified page and slot by replacing its serialized bytes.
    /// </summary>
    /// <param name="pageId">The page containing the tuple.</param>
    /// <param name="slotIndex">The slot index within the page.</param>
    /// <param name="tuple">The updated tuple value.</param>
    protected void UpdateTuple(PageId pageId, int slotIndex, in StorageTuple tuple)
    {
        UpdateRecord(pageId, slotIndex, tuple.ToBytes());
    }

    /// <summary>
    /// Flushes all dirty pages to the underlying data stream, flushes the journal,
    /// and updates the file header.
    /// </summary>
    protected unsafe void Flush()
    {
        UpdateFileHeader();
        _pageManager?.FlushAll();
        _journal?.Flush(forceDurable: true);
    }

    /// <inheritdoc />
    public IStorageUnitIterator GetUnitIterator()
    {
        return new StorageUnitIterator(_pageManager!, _freeSpaceMap);
    }

    /// <summary>
    /// Commits a storage transaction: appends after images of every touched page and
    /// a commit record, then makes the journal durable before returning.
    /// </summary>
    internal unsafe void CommitTransaction(StorageTransaction transaction)
    {
        // Deterministic page order keeps the journal replayable and testable.
        var pageIds = new List<long>(transaction.BeforeImages.Keys);
        pageIds.Sort();

        foreach (long pageId in pageIds)
        {
            using var handle = _pageManager!.GetPage((PageId)pageId);

            var image = new byte[Page.Size];
            new ReadOnlySpan<byte>(handle.Page.Pointer, Page.Size).CopyTo(image);

            long lsn = _journal!.AppendPageImage(
                transaction.Sequence, (PageId)pageId, JournalRecordType.AfterPageImage, image);

            var page = handle.Page;
            page.Lsn = lsn;
            handle.MarkDirty();
        }

        long commitLsn = _journal!.AppendCommit(transaction.Sequence);
        _journal.EnsureDurable(commitLsn);

        ReleasePageWriteLocks(transaction);
    }

    /// <summary>
    /// Rolls a storage transaction back: restores every touched page to its before
    /// image in the buffer pool and appends a rollback record.
    /// </summary>
    internal unsafe void RollbackTransaction(StorageTransaction transaction)
    {
        foreach (var (pageId, image) in transaction.BeforeImages)
        {
            using var handle = _pageManager!.GetPage((PageId)pageId);
            image.CopyTo(new Span<byte>(handle.Page.Pointer, Page.Size));
            handle.MarkDirty();
        }

        _journal!.AppendRollback(transaction.Sequence);

        ReleasePageWriteLocks(transaction);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_disposed)
        {
            try
            {
                ShutdownFlush();
                _journal?.Dispose();
            }
            finally
            {
                _pageManager?.Dispose();
                _bufferPool.Dispose();

                // Dispose all three streams
                Data.Dispose();
                Journal.Dispose();
                Backup.Dispose();

                _disposed = true;
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            try
            {
                ShutdownFlush();

                if (_journal != null)
                {
                    await _journal.DisposeAsync();
                }

                if (_pageManager != null)
                {
                    await _pageManager.DisposeAsync();
                }
            }
            finally
            {
                _bufferPool.Dispose();

                // Dispose all three streams
                await Data.DisposeAsync();
                await Journal.DisposeAsync();
                await Backup.DisposeAsync();

                _disposed = true;
            }
        }
    }

    /// <summary>
    /// Flushes state on shutdown: a clean checkpoint when no transactions are active
    /// (so the next open recovers instantly), otherwise a plain durable flush — the
    /// write-ahead gate has kept the journal ahead of any stolen page, so recovery
    /// will undo whatever the abandoned transactions left behind.
    /// </summary>
    private void ShutdownFlush()
    {
        if (_pageManager is null || _journal is null)
        {
            return;
        }

        bool idle;
        lock (_transactionLock)
        {
            idle = _pageWriteLocks.Count == 0;
        }

        if (idle)
        {
            Checkpoint();
        }
        else
        {
            UpdateFileHeader();
            _pageManager.FlushAll();
            Data.FlushDurable();
            _journal.Flush(forceDurable: true);
        }
    }

    private StorageTransaction ValidateTransaction(IStorageTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        if (transaction is not StorageTransaction owner)
        {
            throw new StorageTransactionException("The transaction was not created by this storage instance.");
        }

        if (!owner.IsActive)
        {
            throw new StorageTransactionException($"Storage transaction {owner.Sequence} has already completed.");
        }

        return owner;
    }

    /// <summary>
    /// Pins a page for modification by a transaction: acquires the page write lock
    /// and captures the before image on first touch.
    /// </summary>
    private IStoragePageHandle TouchPage(StorageTransaction transaction, PageId pageId)
    {
        var handle = _pageManager!.GetPage(pageId);

        try
        {
            RegisterTouch(transaction, handle);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Allocates and initializes a fresh data page inside a transaction. The before
    /// image captures the freshly initialized (empty) page: rollback restores an
    /// allocated-but-empty page rather than deallocating — a safe leak.
    /// </summary>
    private IStoragePageHandle AllocateDataPage(StorageTransaction transaction, out SlottedPage slotted)
    {
        var handle = _pageManager!.AllocatePage(PageType.Data);

        try
        {
            slotted = new SlottedPage(handle.Page);
            slotted.Initialize();
            _currentWritePageId = handle.Id;
            RegisterTouch(transaction, handle);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private unsafe void RegisterTouch(StorageTransaction transaction, IStoragePageHandle handle)
    {
        long pageId = (long)handle.Id;

        lock (_transactionLock)
        {
            if (_pageWriteLocks.TryGetValue(pageId, out long owner))
            {
                if (owner != transaction.Sequence)
                {
                    throw new StorageTransactionException(
                        $"Page {pageId} is write-locked by transaction {owner}.");
                }
            }
            else
            {
                _pageWriteLocks[pageId] = transaction.Sequence;
            }
        }

        if (transaction.HasTouched(pageId))
        {
            return;
        }

        var image = new byte[Page.Size];
        new ReadOnlySpan<byte>(handle.Page.Pointer, Page.Size).CopyTo(image);

        long lsn = _journal!.AppendPageImage(
            transaction.Sequence, handle.Id, JournalRecordType.BeforePageImage, image);

        transaction.RecordBeforeImage(pageId, image);

        // Stamp the page so the write-ahead gate flushes the before image before any
        // stolen write of this page can reach the data file.
        var page = handle.Page;
        page.Lsn = lsn;
    }

    private void ReleasePageWriteLocks(StorageTransaction transaction)
    {
        lock (_transactionLock)
        {
            foreach (long pageId in transaction.BeforeImages.Keys)
            {
                if (_pageWriteLocks.TryGetValue(pageId, out long owner) && owner == transaction.Sequence)
                {
                    _pageWriteLocks.Remove(pageId);
                }
            }
        }
    }

    private unsafe void WriteFileHeader(IStoragePageHandle handle)
    {
        var header = (StorageFileHeader*)(handle.Page.Pointer + Page.HeaderSize);
        header->Magic = StorageFileHeader.ExpectedMagic;
        header->FormatVersion = StorageFileHeader.CurrentFormatVersion;
        header->PageSize = Page.Size;
        header->Model = Model;
        header->TotalPageCount = 2;
        header->FreePageCount = 0;
        header->FreeSpaceMapPageId = 0;
        header->RootSegmentPageId = 1;
        header->CreatedAtUtcTicks = DateTime.UtcNow.Ticks;
        header->ModifiedAtUtcTicks = DateTime.UtcNow.Ticks;

        var guidBytes = ((Guid)_id).ToByteArray();
        for (int i = 0; i < 16; i++)
        {
            header->StorageId[i] = guidBytes[i];
        }

        string? nameStr = (string?)_name;
        if (!string.IsNullOrEmpty(nameStr))
        {
            var nameBytes = Encoding.UTF8.GetBytes(nameStr);
            int len = Math.Min(nameBytes.Length, 128);
            for (int i = 0; i < len; i++)
            {
                header->Name[i] = nameBytes[i];
            }
        }
    }

    private unsafe void UpdateFileHeader()
    {
        if (_pageManager == null || _disposed)
        {
            return;
        }

        using var handle = _pageManager.GetPage((PageId)0L);
        var header = (StorageFileHeader*)(handle.Page.Pointer + Page.HeaderSize);
        header->TotalPageCount = _pageManager.PageCount;
        header->FreePageCount = _pageManager.FreePageCount;
        header->ModifiedAtUtcTicks = DateTime.UtcNow.Ticks;
        handle.MarkDirty();
    }
}
