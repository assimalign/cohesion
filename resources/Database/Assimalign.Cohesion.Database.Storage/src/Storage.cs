using System;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Storage;

using Assimalign.Cohesion.Database.Storage.Internal;
using Assimalign.Cohesion.Database.Storage.Units;

/// <summary>
/// Abstract base class for all storage implementations. Manages three file assets
/// (data, journal, backup), page allocation, buffer caching, and record-level I/O
/// through slotted pages.
/// </summary>
/// <remarks>
/// Derived classes (SQL, Document, Graph, KeyValue) provide model-specific APIs that delegate
/// to the record operations defined here. All models share the same page-based
/// storage infrastructure operating on the <see cref="Data"/> stream, with a
/// per-database journal backed by the <see cref="Journal"/> stream.
/// </remarks>
public abstract class Storage : IStorage
{
    private readonly StorageBufferPool _bufferPool;
    private readonly StorageFreeSpaceMap _freeSpaceMap;
    private StoragePageManager? _pageManager;
    private StreamJournalLogger? _journalLogger;
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
    /// Gets the journal logger for this storage instance.
    /// </summary>
    /// <remarks>
    /// Available to derived classes for model-specific transaction logging.
    /// The session/transaction layer decides when to log — base record operations
    /// do not automatically write journal entries.
    /// </remarks>
    protected IJournalLogger JournalLogger =>
        _journalLogger ?? throw new InvalidOperationException("Storage has not been initialized.");

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

        // Initialize the journal logger from the journal stream
        _journalLogger = new StreamJournalLogger(Journal, leaveOpen: true);

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
    /// Opens an existing storage file set by reading the file header and reconstructing
    /// the internal state (free-space map and current write position) from a single
    /// pass over the on-disk page headers.
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

        // Initialize the journal logger from the journal stream
        _journalLogger = new StreamJournalLogger(Journal, leaveOpen: true);

        _currentWritePageId = lastDataPage;
    }

    /// <summary>
    /// Inserts a record into the storage. Automatically allocates a new data page
    /// if the current page is full.
    /// </summary>
    /// <param name="data">The record data to insert.</param>
    /// <returns>The page identifier and slot index where the record was stored.</returns>
    /// <exception cref="SlottedPageException">The record is larger than a single page can hold.</exception>
    protected unsafe (PageId PageId, int SlotIndex) InsertRecord(ReadOnlySpan<byte> data)
    {
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
            handle = AllocateDataPage(out slotted);
        }
        else
        {
            handle = _pageManager!.GetPage(_currentWritePageId.Value);
            slotted = new SlottedPage(handle.Page);

            if (!slotted.CanFit(data.Length))
            {
                handle.Dispose();
                handle = AllocateDataPage(out slotted);
            }
        }

        int slotIndex = slotted.InsertSlot(data);
        handle.MarkDirty();

        var pageId = handle.Id;
        handle.Dispose();

        return (pageId, slotIndex);
    }

    private IStoragePageHandle AllocateDataPage(out SlottedPage slotted)
    {
        var handle = _pageManager!.AllocatePage(PageType.Data);
        slotted = new SlottedPage(handle.Page);
        slotted.Initialize();
        _currentWritePageId = handle.Id;
        return handle;
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
    /// Deletes a record at the specified page and slot by marking the slot as deleted.
    /// </summary>
    /// <param name="pageId">The page containing the record.</param>
    /// <param name="slotIndex">The slot index within the page.</param>
    protected unsafe void DeleteRecord(PageId pageId, int slotIndex)
    {
        using var handle = _pageManager!.GetPage(pageId);
        var slotted = new SlottedPage(handle.Page);
        slotted.DeleteSlot(slotIndex);
        handle.MarkDirty();
    }

    /// <summary>
    /// Updates a record at the specified page and slot with new data.
    /// </summary>
    /// <param name="pageId">The page containing the record.</param>
    /// <param name="slotIndex">The slot index within the page.</param>
    /// <param name="data">The new record data.</param>
    protected unsafe void UpdateRecord(PageId pageId, int slotIndex, ReadOnlySpan<byte> data)
    {
        using var handle = _pageManager!.GetPage(pageId);
        var slotted = new SlottedPage(handle.Page);
        slotted.UpdateSlot(slotIndex, data);
        handle.MarkDirty();
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
        _journalLogger?.Flush(forceDurable: true);
    }

    /// <inheritdoc />
    public IStorageUnitIterator GetUnitIterator()
    {
        return new StorageUnitIterator(_pageManager!, _freeSpaceMap);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_disposed)
        {
            try
            {
                // Flush data pages and update header
                UpdateFileHeader();
                _pageManager?.FlushAll();

                // Flush and dispose journal
                _journalLogger?.Flush(forceDurable: true);
                _journalLogger?.Dispose();
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
                // Flush data pages and update header
                UpdateFileHeader();
                if (_pageManager != null)
                {
                    await _pageManager.FlushAllAsync();
                    await _pageManager.DisposeAsync();
                }

                // Flush and dispose journal
                _journalLogger?.Flush(forceDurable: true);
                if (_journalLogger != null)
                {
                    await _journalLogger.DisposeAsync();
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
