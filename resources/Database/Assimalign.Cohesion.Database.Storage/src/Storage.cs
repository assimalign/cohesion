using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using System.Threading;
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
/// Derived classes (SQL, Document, Graph, KeyValue, Blob) provide model-specific APIs that
/// delegate to the record operations defined here. All models share the same
/// page-based storage infrastructure operating on the <see cref="Data"/> stream, with
/// a per-database write-ahead log backed by the <see cref="Journal"/> stream. Each logical
/// database an engine manages gets its own instance with isolated file streams.
/// </para>
/// <para>
/// The base is the contract the transaction layer, the indexes and the catalogs program
/// against; there is no interface beside it. Its five leaves live in the model storage
/// assemblies, so the constructor is <c>protected</c>.
/// </para>
/// <para>
/// <b>Durability model (steal / no-force).</b> Record mutations run inside an
/// <see cref="StorageTransaction"/>: the first change of a page since the last checkpoint
/// journals its full image, commit journals the bytes each page changed (a delta) plus a
/// commit record and returns once the journal meets the selected durability policy
/// (storage format 3, #1253). Data pages flush lazily — the
/// buffer pool may steal (evict) dirty pages early because the write-ahead gate
/// flushes the journal first, durably when required by that policy, and commit
/// never forces data pages. Opening a storage file replays the
/// journal in order from the checkpoint: every page is rebuilt from its full image and
/// the committed changes after it, which also overwrites uncommitted work that reached
/// the file.
/// </para>
/// <para>
/// Journal records are appended to a user-space buffer (#1252,
/// <see cref="StorageJournal"/>), which drains to the operating system before every commit is
/// acknowledged, in every durability mode, and before the write-ahead gate lets a page through.
/// </para>
/// <para>
/// The file-header page (page 0) is deliberately unlogged (storage format 2, see
/// <see cref="StorageFileHeader"/>): an identity block written once at creation, and two
/// alternating header slots carrying recomputable bookkeeping, the LSN and transaction
/// sequence floors, and the checkpoint anchor (<see cref="CheckpointActiveTransactions"/>).
/// None of it needs the journal: every checkpoint writes a new header generation into the
/// slot that does not hold the newest one and makes it durable before it truncates the
/// journal, and a write torn by a crash leaves the other slot to open from.
/// </para>
/// </remarks>
// Deviates from the repo interface-first rule per design decision: Database engines are concrete-first — abstract bases with protected cores and sealed model leaves (owner, 2026-10-04; database-area.md).
public abstract class Storage : IAsyncDisposable, IDisposable
{
    private readonly StorageBufferPool _bufferPool;
    private readonly StorageFreeSpaceMap _freeSpaceMap;
    private readonly Dictionary<long, long> _pageWriteLocks = new();
    private readonly object _transactionLock = new();
    private readonly StorageGroupCommitGate _groupCommitGate = new();
    private readonly StorageModel _model;

    // Per-owner record chains: which data pages belong to which owner, and each
    // owner's current write page. Rebuilt from page headers on open (the same scan
    // that rebuilds the free-space map); maintained on allocation and commit-time
    // frees. Guarded by _ownerLock — page headers on disk stay the source of truth.
    // Commit-time frees take _ownerLock inside _transactionLock; nothing takes
    // _transactionLock while it holds _ownerLock.
    private readonly Dictionary<ulong, SortedSet<long>> _ownerPages = new();
    private readonly Dictionary<ulong, PageId> _currentWritePages = new();
    private readonly object _ownerLock = new();

    private StoragePageManager? _pageManager;
    private StorageJournal? _journal;
    private long _nextTransactionSequence;
    private int _activeTransactionCount;

    // The commit durability (a StorageCommitDurability) and the group-commit window in ticks.
    // Written by their setters only, the durability under _transactionLock (owner decision 26 of
    // 2026-10-06): a checkpoint holds that lock throughout, so it reads one value, and a commit
    // reads the field once (CommitTransaction). Read with Volatile, since commits do not hold it.
    private int _commitDurability = (int)StorageCommitDurability.Synchronous;
    private long _groupCommitWindowTicks = TimeSpan.FromMilliseconds(5).Ticks;

    private StorageId _id;
    private Name _name;
    private bool _disposed;

    // The journal position and sequence counter an existing file set opened at,
    // once recovery finished; null for a file set this instance created. Shutdown
    // compares against it to recognize a storage nothing was written through.
    private (long Lsn, long Sequence)? _openedAt;

    // The checkpoint anchor the newest header generation holds: read at open, replaced by
    // every checkpoint.
    private long[] _checkpointActives = [];

    // Page 0's alternating header slots (storage format 2). Header writes are serialized by
    // _headerLock, which a checkpoint takes inside _transactionLock and nothing takes the
    // other way round. _headerSlot holds the newest durable generation; the next write goes
    // to the other slot, and only a write that reached durable storage moves _headerSlot.
    private readonly object _headerLock = new();
    private int _headerSlot;
    private long _headerGeneration;
    private long _lsnFloor;
    private long _sequenceFloor;

    // The identity block every header slot carries a copy of. When open had to take it from a
    // slot (the identity block on page 0 failed its checksum or magic), the next write to slot 0
    // rewrites page 0's whole leading block with it.
    private byte[] _identity = new byte[StorageFileHeader.ByteSize];
    private bool _identityNeedsRepair;

    // Set when a header write failed after its slot write was issued: that slot may already be
    // the newest generation on the media, so no further header write may run in this process.
    // The failure took the storage offline too (#1268), which is what refuses every later write;
    // the flag records which failure it was (diagnostics and tests).
    private bool _headerFaulted;

    // The checkpoint anchor pages each slot's generation chains, in order. A slot owns its
    // chain alone, so rewriting one slot never touches the pages the other slot reads.
    private readonly List<long>[] _anchorChains = [new List<long>(), new List<long>()];

    // The offline latch of a storage that has no journal yet. Once the journal is attached, its
    // latch is the storage's only one: every path that takes the storage offline (a failed drain
    // or fsync of the journal, a failed data-file fsync, a failed header slot write, another file
    // set of the database) sets it, it keeps the first error, and OfflineError reads it, so the
    // error OnOffline was raised with is the one OfflineError reports for the life of the
    // instance (#1243, #1252, #1268).
    private StorageOfflineException? _offline;

    // The hook raised once when the storage goes offline, and 1 once it was raised.
    private Action<StorageOfflineException>? _onOffline;
    private int _offlineRaised;

    // The checkpoint size trigger (forwarded to the journal once it exists) and the bookkeeping
    // IsCheckpointDue reads: when the last checkpoint completed (or the storage was created or
    // opened), and the journal's last LSN at that moment.
    private long _checkpointJournalSize;
    private Action? _onCheckpointNeeded;
    private long _lastCheckpointTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
    private long _lastCheckpointLsn;

    // The redo point (#1253): the LSN of the checkpoint the journal starts at, or the header's LSN
    // floor when that is higher. A page whose LSN is at or below it has no record in the journal,
    // so its first change journals a full page image (invariant P). Written only while no storage
    // transaction is active (open, checkpoint), so a transaction reads one value throughout.
    private long _redoLsn;

    // The pre-images of active storage transactions (#1253): the bytes they keep in memory, the
    // peak of that total, the pre-images spilled to the journal, and the budget past which a
    // transaction spills. A zero budget means the default (PreImageBudget).
    private long _preImageBytes;
    private long _peakPreImageBytes;
    private long _spilledPreImages;
    private long _preImageBudget;

    // The debug consistency check (#1253): null unless requested by the environment or a test.
    private bool _consistencyRequested = StorageConsistencyCheck.RequestedByEnvironment;
    private StorageConsistencyCheck? _consistency;

    /// <summary>
    /// The buffer pool capacity a storage gets when its constructor is not given one: 4,096
    /// pages, 32 MiB of page buffers (#1254).
    /// </summary>
    /// <remarks>
    /// The pool allocates its 8 KiB buffers as pages are first loaded, so a storage that touches
    /// fewer pages costs less; a storage under load reaches the capacity and stays there. The
    /// previous default, 128 pages (1 MiB), could not keep a 4 MiB index resident, and random
    /// inserts into one paid a steal and a reload per touch (#1236).
    /// </remarks>
    public const int DefaultBufferPoolCapacity = 4096;

    /// <summary>
    /// The smallest buffer pool, in bytes, an engine accepts for a database: 1 MiB, 128 pages,
    /// the pool every engine ran with before #1254, which holds every page one operation pins at
    /// once with room to spare.
    /// </summary>
    public const long MinimumBufferPoolBytes = 128L * Page.Size;

    /// <summary>
    /// Converts a buffer pool capacity in bytes, as engine options state it, to pages, after
    /// checking it: a whole number of 8 KiB pages, at least <see cref="MinimumBufferPoolBytes"/>,
    /// and no more pages than an <see cref="int"/> counts.
    /// </summary>
    /// <param name="capacityBytes">The capacity in bytes.</param>
    /// <param name="paramName">The name of the option, for the exception.</param>
    /// <returns>The capacity in pages.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The capacity is not a valid pool size.</exception>
    public static int GetBufferPoolPageCount(long capacityBytes, string paramName = "capacityBytes")
    {
        if (capacityBytes < MinimumBufferPoolBytes || capacityBytes % Page.Size != 0 || capacityBytes / Page.Size > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(paramName, capacityBytes,
                $"A buffer pool capacity must be a whole number of {Page.Size}-byte pages, at least {MinimumBufferPoolBytes} bytes " +
                $"({MinimumBufferPoolBytes / Page.Size} pages) and at most {(long)int.MaxValue * Page.Size} bytes.");
        }

        return (int)(capacityBytes / Page.Size);
    }

    /// <summary>
    /// Initializes the storage with the specified backing streams for data, journal, and backup.
    /// </summary>
    /// <param name="model">The storage model the leaf implements; fixed for the life of the instance.</param>
    /// <param name="data">The data stream providing page-level I/O for the <c>.dat</c> file.</param>
    /// <param name="journal">The journal stream for the <c>.log</c> file (write-ahead log).</param>
    /// <param name="backup">The backup stream for the <c>.bak</c> file.</param>
    /// <param name="bufferPoolCapacity">
    /// Maximum number of pages to cache in memory; <see cref="DefaultBufferPoolCapacity"/>
    /// (32 MiB) unless given. <see cref="BufferPoolCapacity"/> changes it later.
    /// </param>
    protected Storage(StorageModel model, StorageStream data, StorageStream journal, StorageStream backup, int bufferPoolCapacity = DefaultBufferPoolCapacity)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(backup);

        _model = model;
        Data = data;
        Journal = journal;
        Backup = backup;
        _bufferPool = new StorageBufferPool(bufferPoolCapacity) { WriteGuard = ThrowIfOffline };
        _freeSpaceMap = new StorageFreeSpaceMap();
    }

    /// <summary>
    /// Gets the unique identifier for this storage resource.
    /// </summary>
    public StorageId Id => _id;

    /// <summary>
    /// Gets the name of this storage resource.
    /// </summary>
    public Name Name => _name;

    /// <summary>
    /// Gets the storage model implemented within this storage resource.
    /// </summary>
    /// <remarks>
    /// Fixed per storage, so the leaf passes it to the protected constructor and the getter reads
    /// a field (<c>database-area.md</c>, type shape rule 6).
    /// </remarks>
    public StorageModel Model => _model;

    /// <summary>
    /// Gets the data stream providing page-level I/O for the <c>.dat</c> file.
    /// </summary>
    /// <remarks>
    /// All page-based operations (record storage, indexes, catalog metadata) are performed
    /// against this stream through the <see cref="PageManager"/>.
    /// </remarks>
    public StorageStream Data { get; }

    /// <summary>
    /// Gets the journal stream providing sequential I/O for the <c>.log</c> file.
    /// </summary>
    /// <remarks>
    /// The journal stream backs the write-ahead log that guarantees ACID durability.
    /// Transaction records are appended sequentially and flushed on commit.
    /// </remarks>
    public StorageStream Journal { get; }

    /// <summary>
    /// Gets the backup stream for the <c>.bak</c> file.
    /// </summary>
    /// <remarks>
    /// Used for point-in-time backup snapshots. The backup stream is separate from
    /// the data and journal streams to avoid contention during normal operations.
    /// </remarks>
    public StorageStream Backup { get; }

    /// <summary>
    /// Gets the page manager that coordinates page allocation, retrieval, and flushing
    /// against the <see cref="Data"/> stream.
    /// </summary>
    public StoragePageManager PageManager =>
        _pageManager ?? throw new InvalidOperationException("Storage has not been initialized.");

    /// <summary>
    /// Gets the buffer pool that caches pages in memory with pin-counting and LRU eviction.
    /// Only the storage itself and its own tests read it.
    /// </summary>
    internal StorageBufferPool BufferPool =>
        _bufferPool ?? throw new InvalidOperationException("Storage has not been initialized.");

    /// <summary>
    /// Gets the free space map that tracks allocated and free pages in the data file.
    /// </summary>
    public StorageFreeSpaceMap FreeSpaceMap =>
        _freeSpaceMap ?? throw new InvalidOperationException("Storage has not been initialized.");

    /// <summary>
    /// Gets or sets the number of pages the buffer pool may hold; 8 KiB each
    /// (<see cref="DefaultBufferPoolCapacity"/> unless the constructor was given another).
    /// </summary>
    /// <remarks>
    /// Growing takes effect at once. Shrinking evicts the least recently used unpinned pages,
    /// writing dirty ones back first through the write-ahead gate, until the resident pages fit.
    /// Engines set it from their buffer-pool option right after a storage is created or opened,
    /// before any work runs on it.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than one page.</exception>
    /// <exception cref="StorageIOException">More pages than the new capacity are pinned; the pool is unchanged.</exception>
    public int BufferPoolCapacity
    {
        get => _bufferPool.Capacity;
        set => _bufferPool.Resize(value, Data);
    }

    /// <summary>
    /// Gets the number of bytes the journal holds since its last truncation, the records still in
    /// its append buffer included (#1252): what the next checkpoint discards, and what a recovery
    /// reads once the buffer has drained. Zero before the storage is initialized.
    /// </summary>
    public long JournalLength => _journal?.Length ?? 0;

    /// <summary>
    /// Gets or sets the journal length, in bytes, at which the storage asks for a checkpoint
    /// (#1254): the first append that takes <see cref="JournalLength"/> to this size or past it
    /// invokes <see cref="OnCheckpointNeeded"/>, once per checkpoint cycle, and
    /// <see cref="IsCheckpointDue"/> reports the storage due. Zero (the default) disables the
    /// size trigger.
    /// </summary>
    /// <remarks>
    /// PostgreSQL requests a checkpoint the same way once the WAL written since the last redo
    /// point passes its share of <c>max_wal_size</c> (<c>XLogCheckpointNeeded</c> and
    /// <c>RequestCheckpoint(CHECKPOINT_CAUSE_XLOG)</c> in <c>XLogWrite</c>,
    /// <c>src/backend/access/transam/xlog.c:2358-2367</c>, <c>2579-2584</c>).
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public long CheckpointJournalSize
    {
        get => Volatile.Read(ref _checkpointJournalSize);
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            Volatile.Write(ref _checkpointJournalSize, value);
            _journal?.ConfigureCheckpointTrigger(value, _onCheckpointNeeded);
        }
    }

    /// <summary>
    /// Gets or sets the hook invoked when an append takes the journal to
    /// <see cref="CheckpointJournalSize"/> or past it, so an engine's checkpoint worker can be
    /// woken. Invoked outside storage locks, at most once per checkpoint cycle.
    /// </summary>
    public Action? OnCheckpointNeeded
    {
        get => _onCheckpointNeeded;
        set
        {
            _onCheckpointNeeded = value;
            _journal?.ConfigureCheckpointTrigger(CheckpointJournalSize, value);
        }
    }

    /// <summary>
    /// Gets the error that took this storage offline, or null while it is online: a durable
    /// flush of its journal or its data file failed (#1243), a write of the journal's append
    /// buffer did (#1252), or a write of its file header failed after the header slot write was
    /// issued (#1268). Once set it stays set for the life of this instance, and it is the error
    /// <see cref="OnOffline"/> was raised with: when two failures race, the first is kept and the
    /// later one is refused with it. Only reopening the storage, which runs recovery, brings the
    /// file set back.
    /// </summary>
    /// <remarks>
    /// While offline the storage writes nothing: every journal append, flush and checkpoint,
    /// every page write-back and file extension, every header write, every new storage
    /// transaction and every record change throws <see cref="StorageOfflineException"/>, and
    /// closing it writes nothing either. Reads of resident and on-disk pages still work; the
    /// engines refuse every operation of an offline database before it reaches the storage.
    /// </remarks>
    public StorageOfflineException? OfflineError => _journal?.OfflineError ?? Volatile.Read(ref _offline);

    /// <summary>
    /// Gets whether a failed durable flush, journal write or file header write took this storage
    /// offline (see <see cref="OfflineError"/>).
    /// </summary>
    public bool IsOffline => OfflineError is not null;

    /// <summary>
    /// Takes this storage offline because another file set of the same database went offline:
    /// an engine whose database spans several storages (a data set and a catalog set) stops
    /// writing to all of them when one fails, so no file of the database changes after the
    /// failure, closing included. Has no effect on a storage already offline.
    /// </summary>
    /// <param name="error">The error that took the other file set offline.</param>
    /// <remarks>
    /// Engines call it from the other storage's <see cref="OnOffline"/>, so the second file set
    /// goes offline when the first does, not when something next reads either state.
    /// </remarks>
    public void TakeOffline(StorageOfflineException error)
    {
        ArgumentNullException.ThrowIfNull(error);

        // At once on a storage already offline, without the journal's lock (see OnOffline).
        if (!IsOffline)
        {
            Latch(error);
        }
    }

    /// <summary>
    /// Gets or sets the hook invoked once, with the error, when this storage goes offline: a
    /// durable flush of its journal or its data file failed (#1243), a write of the journal failed
    /// (#1252), a write of its file header failed after the slot write was issued (#1268), or
    /// <see cref="TakeOffline"/> was called. An engine whose database spans several storages takes
    /// the others offline from it, so none of them is written after the failure.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Raised by the call that took the storage offline, before that call returns or throws, so
    /// whoever sees the failure sees it after the hook ran. It runs outside this storage's
    /// journal lock and group-commit lock, which is what lets a handler take another storage
    /// offline even while that storage is going offline through its own hook: the two storages
    /// never wait on each other's journal. It may run while the failing call holds this
    /// storage's header, transaction or buffer-pool lock, so a handler must not call back into
    /// this storage, except <see cref="TakeOffline"/>, which returns at once on a storage
    /// already offline. It should only take other storages offline, or end the database's lock
    /// waits (the transaction coordinator's <c>AbandonLockWaits</c>, which does no lock-table work
    /// on the calling thread), and must not throw.
    /// </para>
    /// <para>
    /// Set it before the storage does any work; a storage already offline when it is set does
    /// not raise it again.
    /// </para>
    /// </remarks>
    public Action<StorageOfflineException>? OnOffline
    {
        get => Volatile.Read(ref _onOffline);
        set => Volatile.Write(ref _onOffline, value);
    }

    /// <summary>
    /// Raises <see cref="OnOffline"/> once, whichever path took the storage offline first.
    /// </summary>
    /// <param name="error">The error that took the storage offline.</param>
    private void RaiseOffline(StorageOfflineException error)
    {
        if (Interlocked.Exchange(ref _offlineRaised, 1) == 0)
        {
            OnOffline?.Invoke(error);
        }
    }

    /// <summary>
    /// Reports whether this storage should be checkpointed now: it is online, and either the
    /// journal reached <see cref="CheckpointJournalSize"/> (when that is set), or
    /// <paramref name="interval"/> has passed since the last checkpoint (or since the storage
    /// was created or opened) and something was written to the journal since then.
    /// </summary>
    /// <param name="interval">The time backstop: the longest a written journal waits for a checkpoint.</param>
    /// <returns>True when a checkpoint is due.</returns>
    /// <remarks>
    /// The size trigger bounds recovery time and journal disk use under load; the time backstop
    /// bounds them for a slow trickle of writes, as PostgreSQL's <c>checkpoint_timeout</c> does
    /// (<c>CheckpointerMain</c>, <c>src/backend/postmaster/checkpointer.c:405-412</c>). Like
    /// PostgreSQL, which skips a checkpoint when no important WAL was written since the last one
    /// (<c>CreateCheckPoint</c>, <c>src/backend/access/transam/xlog.c:7759-7775</c>), an idle
    /// storage is not checkpointed by time alone.
    /// </remarks>
    public bool IsCheckpointDue(TimeSpan interval)
    {
        if (_journal is not { } journal || IsOffline)
        {
            return false;
        }

        long threshold = CheckpointJournalSize;
        if (threshold > 0 && journal.Length >= threshold)
        {
            return true;
        }

        return journal.LastLsn > Volatile.Read(ref _lastCheckpointLsn)
            && System.Diagnostics.Stopwatch.GetElapsedTime(Volatile.Read(ref _lastCheckpointTimestamp)) >= interval;
    }

    /// <summary>
    /// Gets the write-ahead log for this storage instance.
    /// </summary>
    /// <remarks>
    /// Available to derived classes and the transaction layer for logical operation
    /// records. Physical durability (page images, commit records, recovery) is
    /// managed by the storage transaction scope — derived classes should not append
    /// page images directly.
    /// </remarks>
    protected StorageJournal WriteAheadLog =>
        _journal ?? throw new InvalidOperationException("Storage has not been initialized.");

    /// <summary>
    /// Gets the logical transaction sequences the last checkpoint recorded as in flight,
    /// as the file header's checkpoint anchor holds them: read when an existing file set
    /// opens, and replaced by every checkpoint.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A checkpoint truncates the journal, which destroys the begin records of the logical
    /// transactions still in flight while their row versions stay in the data pages. Its
    /// checkpoint record lists those transactions, but the record is appended after the
    /// truncation: when that append fails, or the process stops between the truncation and
    /// the record's flush, the journal no longer names them, and a reader would take their
    /// versions for committed ones. So the checkpoint first writes the same list into a new
    /// header generation of page 0 and makes it durable before the truncation starts.
    /// </para>
    /// <para>
    /// The transaction layer passes only its writers — transactions that can have stamped
    /// row versions or still owe an undo; a reader stamps nothing and needs no entry — and
    /// the anchor has no capacity limit: a header slot holds the first sequences itself and
    /// chains <see cref="PageType.CheckpointAnchor"/> pages for the rest (storage format 2).
    /// </para>
    /// <para>
    /// The transaction layer's recovery treats every sequence listed here exactly like one
    /// listed in a checkpoint record: aborted, unless the journal holds its commit record
    /// (<c>TransactionRecovery.Analyze</c> in <c>Database.Transactions</c>). Page 0 is not
    /// journaled, so the list survives only through that durable write; an open whose
    /// checkpoint is not deferred replaces it before anyone reads it, as it truncates the
    /// journal.
    /// </para>
    /// </remarks>
    public IReadOnlyList<long> CheckpointActiveTransactions => Volatile.Read(ref _checkpointActives);

    /// <summary>
    /// Gets the newest durable header generation, the slot of page 0 holding it, the LSN
    /// floor it persisted and its checkpoint anchor chain (diagnostics and tests).
    /// </summary>
    internal (long Generation, int Slot, long LsnFloor, IReadOnlyList<long> AnchorChain) HeaderState
    {
        get
        {
            lock (_headerLock)
            {
                return (_headerGeneration, _headerSlot, _lsnFloor, _anchorChains[_headerSlot].ToArray());
            }
        }
    }

    /// <summary>
    /// Gets whether open took the identity from a header slot because page 0's identity block
    /// did not verify, and the next write to slot 0 has yet to restore it (diagnostics and tests).
    /// </summary>
    internal bool IdentityRepairPending
    {
        get
        {
            lock (_headerLock)
            {
                return _identityNeedsRepair;
            }
        }
    }

    /// <summary>
    /// Gets the number of storage-level transactions begun and not yet completed (diagnostics and
    /// tests): a checkpoint runs only while it is zero.
    /// </summary>
    internal int ActiveTransactions
    {
        get
        {
            lock (_transactionLock)
            {
                return _activeTransactionCount;
            }
        }
    }

    /// <summary>
    /// Gets whether an active transaction holds the page's write lock (diagnostics and tests).
    /// </summary>
    /// <param name="pageId">The page.</param>
    internal bool IsPageWriteLocked(PageId pageId)
    {
        lock (_transactionLock)
        {
            return _pageWriteLocks.ContainsKey((long)pageId);
        }
    }

    /// <summary>
    /// Gets whether a header write failed after its slot write was issued, which took this
    /// instance offline until the storage is reopened (diagnostics and tests).
    /// </summary>
    internal bool HeaderFaulted
    {
        get
        {
            lock (_headerLock)
            {
                return _headerFaulted;
            }
        }
    }

    /// <summary>
    /// The smallest pre-image budget of a storage transaction: 16 MiB, about 2,700 pre-images of
    /// three-quarters-full pages.
    /// </summary>
    internal const long MinimumPreImageBudget = 16L * 1024 * 1024;

    /// <summary>
    /// Gets or sets how many bytes of pre-images one storage transaction keeps in memory before it
    /// spills the rest to the journal (#1253; Storage DESIGN.md, "The memory bound of
    /// pre-images"). Zero, the default, means half the buffer pool's capacity in bytes, at least
    /// <see cref="MinimumPreImageBudget"/>: 16 MiB for the default 32 MiB pool. Set it only while no
    /// storage transaction is active (tests).
    /// </summary>
    internal long PreImageBudget
    {
        get
        {
            long configured = Volatile.Read(ref _preImageBudget);
            return configured > 0
                ? configured
                : Math.Max(MinimumPreImageBudget, (long)_bufferPool.Capacity * Page.Size / 2);
        }
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            Volatile.Write(ref _preImageBudget, value);
        }
    }

    /// <summary>
    /// Gets the bytes the pre-images of the active storage transactions keep in memory
    /// (diagnostics and tests).
    /// </summary>
    internal long PreImageBytes => Interlocked.Read(ref _preImageBytes);

    /// <summary>
    /// Gets the most bytes the pre-images of active storage transactions held in memory at once
    /// since the storage was opened or created (diagnostics and tests).
    /// </summary>
    internal long PeakPreImageBytes => Interlocked.Read(ref _peakPreImageBytes);

    /// <summary>
    /// Gets the number of pre-images spilled to the journal since the storage was opened or
    /// created (diagnostics and tests).
    /// </summary>
    internal long SpilledPreImages => Interlocked.Read(ref _spilledPreImages);

    /// <summary>
    /// Gets the redo point: the LSN at or below which a page has no record in the journal, so its
    /// next change journals a full page image first (diagnostics and tests).
    /// </summary>
    internal long RedoLsn => Volatile.Read(ref _redoLsn);

    /// <summary>
    /// Gets the debug consistency check, or null while it is off (tests).
    /// </summary>
    internal StorageConsistencyCheck? ConsistencyCheck => _consistency;

    /// <summary>
    /// Turns the debug consistency check on (<see cref="StorageConsistencyCheck"/>), as the
    /// <c>COHESION_STORAGE_CONSISTENCY_CHECKS</c> environment variable does for every storage of a
    /// process. Call it while no storage transaction is active; pages imaged before it was turned
    /// on get their shadows from the journal when next touched.
    /// </summary>
    internal void EnableConsistencyChecks()
    {
        _consistencyRequested = true;
        if (_journal is { } journal && _consistency is null)
        {
            StartConsistencyCheck(journal);
        }
    }

    /// <summary>
    /// Creates the debug consistency check over the journal and hooks its write-back audit into
    /// the buffer pool.
    /// </summary>
    private void StartConsistencyCheck(StorageJournal journal)
    {
        var check = new StorageConsistencyCheck(journal);
        _consistency = check;
        _bufferPool.WriteBackAudit = (pageId, pageLsn) => check.CheckWriteBack(pageId, pageLsn, Volatile.Read(ref _redoLsn));
    }

    /// <summary>
    /// Gets or sets how commits reach stable storage. The default,
    /// <see cref="StorageCommitDurability.Synchronous"/>, flushes the journal durably
    /// inside every commit; <see cref="StorageCommitDurability.Grouped"/> batches
    /// concurrent commits behind one durable flush performed by a flush worker
    /// (see <see cref="FlushPendingCommits"/>). Both modes acknowledge a commit only
    /// after its records are durable. <see cref="StorageCommitDurability.None"/>
    /// retains the same commit records without requesting or claiming durability.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Before the storage is initialized any change is allowed. Each model storage's
    /// <c>Create</c>/<c>Open</c> resolves the setting through
    /// <see cref="ConfigureCommitDurability"/> before it initializes the storage; the Sql and
    /// KeyValuePair engines then apply the same option again to the initialized storage, which never
    /// changes the value (owner decision 26 of 2026-10-06).
    /// </para>
    /// <para>
    /// Once it is initialized, the two durable modes may replace each other at any time,
    /// transactions active or not, as PostgreSQL's <c>synchronous_commit</c> may
    /// (<c>PGC_USERSET</c>, <c>src/backend/utils/misc/guc_parameters.dat:2973</c>): "the behavior
    /// for any one transaction is determined by the setting in effect when it commits"
    /// (<c>doc/src/sgml/config.sgml:3458-3460</c>; <c>RecordTransactionCommit</c> reads it once,
    /// <c>src/backend/access/transam/xact.c:1540-1542</c>). Here a commit reads it once, when its
    /// storage bracket starts to commit, and uses that one value for its durability check and its
    /// wait; a logical commit's later record reads it once in <see cref="EnsureCommitDurable"/>.
    /// Both durable modes flush the data file and the journal alike and differ only in who issues a
    /// commit's fsync, so a checkpoint is durable under either. The setter takes the lock a
    /// checkpoint holds throughout, so a change waits for a running checkpoint and a checkpoint
    /// reads one value from start to end.
    /// </para>
    /// <para>
    /// <see cref="StorageCommitDurability.None"/> is fixed once the storage is initialized: a change
    /// into it or out of it is refused, and setting the current value stays allowed.
    /// <c>None</c> is PostgreSQL's <c>fsync = off</c>, not <c>synchronous_commit = off</c>
    /// (<c>config.sgml:3412-3418</c> contrasts the two): a checkpoint under it truncates the
    /// journal without flushing the data file durably, so the truncation can reach the media ahead
    /// of the pages it stands for. Entering it would expose every commit acknowledged durable before
    /// the change to that truncation: a logical commit still waiting under the durable mode would
    /// make the truncation durable with its own fsync while the pages stay volatile, and a power loss
    /// would then take that commit and the earlier ones with it. Leaving it, the first durable
    /// journal flush would do the same to a truncation made under <c>None</c>. PostgreSQL changes <c>fsync</c> only through its configuration file
    /// (<c>PGC_SIGHUP</c>, <c>guc_parameters.dat:1117</c>; <c>config.sgml:3375-3376</c>), warns that
    /// turning it off risks "unrecoverable data corruption" (<c>config.sgml:3339-3341</c>), and
    /// forces all modified buffers to durable storage before it is turned back on
    /// (<c>config.sgml:3360-3365</c>). The equivalent here is a reopen with the new mode configured
    /// before the open; a durable open's recovery checkpoint flushes the data file durably before it
    /// truncates the journal.
    /// </para>
    /// <para>
    /// The setter does not check that the backing store can flush durably; a commit asking for a
    /// durability its journal cannot provide is refused before anything is journaled (#1018).
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined durability mode.</exception>
    /// <exception cref="InvalidOperationException">
    /// The storage is initialized, and the value enters or leaves
    /// <see cref="StorageCommitDurability.None"/>.
    /// </exception>
    public StorageCommitDurability CommitDurability
    {
        get => (StorageCommitDurability)Volatile.Read(ref _commitDurability);
        set
        {
            if (value is not (StorageCommitDurability.Synchronous or StorageCommitDurability.Grouped or StorageCommitDurability.None))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown commit durability setting.");
            }

            lock (_transactionLock)
            {
                var current = (StorageCommitDurability)_commitDurability;
                if (_journal is not null
                    && (current == StorageCommitDurability.None) != (value == StorageCommitDurability.None))
                {
                    throw new InvalidOperationException(
                        $"Storage '{_name}' cannot change CommitDurability from '{current}' to '{value}' once it is initialized: 'None' " +
                        "checkpoints truncate the journal without flushing the data file durably, so a change into or out of 'None' " +
                        "could make a truncation durable ahead of its pages. Reopen it with the new mode configured before the open.");
                }

                Volatile.Write(ref _commitDurability, (int)value);
            }
        }
    }

    /// <summary>
    /// Gets whether both the data and journal handles can flush to durable storage.
    /// The backup handle does not participate in commit or checkpoint durability.
    /// </summary>
    public bool SupportsDurableFlush => Data.SupportsDurableFlush && Journal.SupportsDurableFlush;

    /// <summary>
    /// Resolves an unset durability choice from the backing handles, or validates
    /// an explicit choice before an engine begins using this storage.
    /// </summary>
    /// <param name="durability">The explicit choice, or null to derive the default.</param>
    /// <param name="storageName">The store name used in configuration errors.</param>
    /// <exception cref="NotSupportedException">An explicit durable setting cannot be provided by the backing store.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The setting is not a defined durability mode.</exception>
    /// <exception cref="InvalidOperationException">
    /// Called after initialization with a setting that enters or leaves
    /// <see cref="StorageCommitDurability.None"/> (see <see cref="CommitDurability"/>).
    /// </exception>
    public void ConfigureCommitDurability(StorageCommitDurability? durability, string storageName)
    {
        var resolved = durability ?? (SupportsDurableFlush
            ? StorageCommitDurability.Synchronous
            : StorageCommitDurability.None);

        if (resolved is not (StorageCommitDurability.Synchronous or StorageCommitDurability.Grouped or StorageCommitDurability.None))
        {
            throw new ArgumentOutOfRangeException(nameof(durability), durability, "Unknown commit durability setting.");
        }

        if (resolved != StorageCommitDurability.None && !SupportsDurableFlush)
        {
            throw new NotSupportedException(
                $"Storage '{storageName}' cannot provide Durability '{resolved}': its backing store does not support durable flush (SupportsDurableFlush = false).");
        }

        CommitDurability = resolved;
    }

    /// <summary>
    /// Applies this storage's durability policy to an already appended commit
    /// record. Non-durable mode makes no durable request or promise, but it still drains the
    /// journal's append buffer through the record, so a process crash never loses a commit
    /// that was acknowledged (#1252).
    /// </summary>
    /// <param name="lsn">The appended commit record's log sequence number.</param>
    /// <remarks>
    /// Reads <see cref="CommitDurability"/> once: the setting in effect when the call starts
    /// decides the whole wait, whatever a concurrent change sets.
    /// </remarks>
    /// <exception cref="StorageOfflineException">
    /// The storage is offline, or the drain or durable flush this call made failed and took it
    /// offline.
    /// </exception>
    public void EnsureCommitDurable(long lsn)
    {
        if (_journal is null)
        {
            throw new InvalidOperationException("Storage has not been initialized.");
        }

        AwaitCommitDurable(lsn, CommitDurability);
    }

    /// <summary>
    /// Applies one durability setting, read once by the caller, to an appended commit record.
    /// </summary>
    /// <param name="lsn">The appended commit record's log sequence number.</param>
    /// <param name="durability">The setting the commit read.</param>
    private void AwaitCommitDurable(long lsn, StorageCommitDurability durability)
    {
        switch (durability)
        {
            case StorageCommitDurability.None:
                // The record leaves the process before the commit is acknowledged, as it did when
                // every append was its own write: only a power loss can take it now.
                _journal!.EnsureWritten(lsn);
                return;
            case StorageCommitDurability.Grouped:
                _groupCommitGate.AwaitDurable(lsn, GroupCommitWindow, _journal!);
                return;
            case StorageCommitDurability.Synchronous:
                _journal!.EnsureDurable(lsn);
                return;
            default:
                // The setter accepts only defined values.
                throw new InvalidOperationException($"Unknown commit durability setting '{durability}'.");
        }
    }

    /// <summary>
    /// The longest <see cref="GroupCommitWindow"/>: the longest timeout a monitor wait takes,
    /// <see cref="int.MaxValue"/> milliseconds (about 24.8 days).
    /// </summary>
    public static readonly TimeSpan MaximumGroupCommitWindow = TimeSpan.FromMilliseconds(int.MaxValue);

    /// <summary>
    /// Gets or sets the bounded window a grouped commit waits for the flush worker
    /// before flushing inline itself. Only meaningful when
    /// <see cref="CommitDurability"/> is <see cref="StorageCommitDurability.Grouped"/>.
    /// </summary>
    /// <remarks>
    /// Zero makes every grouped commit flush inline at once. Each wait reads the window once, so
    /// a change applies from the next grouped commit; a commit already waiting keeps its window.
    /// The bound is a <see cref="Monitor.Wait(object, TimeSpan)"/> timeout's: a longer window
    /// failed the waiting commit after its record was journaled, leaving it unconfirmed (owner
    /// decision 26 of 2026-10-06).
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value is negative or longer than <see cref="MaximumGroupCommitWindow"/>.
    /// </exception>
    public TimeSpan GroupCommitWindow
    {
        get => TimeSpan.FromTicks(Volatile.Read(ref _groupCommitWindowTicks));
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, MaximumGroupCommitWindow);
            Volatile.Write(ref _groupCommitWindowTicks, value.Ticks);
        }
    }

    /// <summary>
    /// Gets or sets the hook invoked when a grouped commit registers for durability,
    /// so an engine-level flush worker can be woken. Invoked outside storage locks.
    /// </summary>
    public Action? OnCommitPending
    {
        get => _groupCommitGate.CommitPending;
        set => _groupCommitGate.CommitPending = value;
    }

    /// <summary>
    /// Creates a new storage file set with the specified name, writing the file header
    /// and allocating the first data page.
    /// </summary>
    /// <param name="name">The name for this storage instance.</param>
    protected unsafe void InitializeNew(Name name)
    {
        _name = name;
        _id = StorageId.NewId();
        _pageManager = new StoragePageManager(Data, _bufferPool, _freeSpaceMap) { WroteOutsideJournal = ForgetShadow };
        AttachJournal();

        // Page 0: the identity block and header slot 0 at generation 1 (slot 1 stays zero,
        // which never verifies). Written directly — page 0 never enters the buffer pool, and
        // the page manager reserves it.
        StorageHeaderPage.ComposeIdentity(_identity, _id, _name, Model, DateTime.UtcNow.Ticks);
        var headerPage = new byte[Page.Size];
        StorageHeaderPage.ComposePageStart(headerPage, _identity);
        StorageHeaderPage.WriteSlot(
            headerPage.AsSpan(StorageHeaderPage.Slot0Offset, StorageHeaderPage.SlotSize),
            new StorageHeaderSlot(
                Generation: 1,
                LsnFloor: 0,
                SequenceFloor: 0,
                TotalPageCount: 2,
                FreePageCount: 0,
                ModifiedAtUtcTicks: DateTime.UtcNow.Ticks,
                AnchorCount: 0,
                AnchorInlineCount: 0,
                AnchorChainHead: 0,
                AnchorChainPageCount: 0),
            _identity,
            ReadOnlySpan<long>.Empty);
        Data.WritePage((PageId)0L, headerPage);
        _headerSlot = 0;
        _headerGeneration = 1;

        // Allocate first data page (page 1) — the shared (owner-zero) space's
        // initial write page.
        var dataHandle = _pageManager.AllocatePage(PageType.Data);
        var slotted = new SlottedPage(dataHandle.Page);
        slotted.Initialize();
        dataHandle.MarkDirty();
        RegisterOwnerPage(0, dataHandle.Id);
        dataHandle.Dispose();

        _pageManager.FlushAll();
        _consistency?.AllWrittenBack();
        MarkCheckpointed();

        // The journal is empty and every page carries LSN zero: each one's first change journals a
        // full page image.
        Volatile.Write(ref _redoLsn, _journal!.LastLsn);
    }

    /// <summary>
    /// Creates the journal over the journal stream and wires it to the storage: the buffer
    /// pool's write-ahead gate, the group-commit waiters it releases when it goes offline, and
    /// the checkpoint size trigger.
    /// </summary>
    private void AttachJournal()
    {
        var journal = StorageJournal.Create(Journal, leaveOpen: true);
        journal.WentOffline = _groupCommitGate.Abandon;
        journal.Offline = RaiseOffline;
        journal.ConfigureCheckpointTrigger(CheckpointJournalSize, _onCheckpointNeeded);
        _journal = journal;
        _bufferPool.WriteAheadGate = FlushWriteAhead;
        if (_consistencyRequested)
        {
            StartConsistencyCheck(journal);
        }
    }

    /// <summary>
    /// Records that the journal holds nothing a checkpoint would need to make durable: after a
    /// checkpoint, and when the storage was created or opened.
    /// </summary>
    private void MarkCheckpointed()
    {
        Volatile.Write(ref _lastCheckpointLsn, _journal?.LastLsn ?? 0);
        Volatile.Write(ref _lastCheckpointTimestamp, System.Diagnostics.Stopwatch.GetTimestamp());
    }

    /// <summary>
    /// Opens an existing storage file set: validates the file header, replays the
    /// write-ahead log (ordered redo of every page's full image and the committed changes
    /// after it, which overwrites uncommitted work), reconstructs the
    /// free-space map from page headers, and — unless the caller defers it —
    /// checkpoints so the journal starts clean.
    /// </summary>
    /// <param name="checkpointOnOpen">
    /// When true (the default), a journal with records is checkpointed (truncated)
    /// once recovery completes. An engine that must analyze the recovered journal
    /// first — transaction-recovery classification reads lifecycle records the
    /// truncation would destroy — passes false and checkpoints itself afterwards.
    /// </param>
    /// <exception cref="StorageFormatException">
    /// The file is a Cohesion storage file in another storage format, or its journal holds a
    /// verified frame of another frame format; there is no upgrade path (#1152).
    /// </exception>
    /// <exception cref="StorageCorruptionException">
    /// The identity block of page 0 fails its checksum, neither header slot verifies, or the
    /// newest slot's checkpoint anchor chain does not verify.
    /// </exception>
    /// <exception cref="StorageIOException">The file is not a Cohesion storage file.</exception>
    protected unsafe void OpenExisting(bool checkpointOnOpen = true)
    {
        var header = ReadHeader(out long[] anchor, out List<long> anchorChain);
        _checkpointActives = anchor;
        _anchorChains[_headerSlot].AddRange(anchorChain);
        var protectedPages = new HashSet<long>(anchorChain);

        // Recover before anything reads pages: replay, in order, every page's full image and the
        // committed changes after it, which also overwrites the stolen writes of transactions that
        // never committed (#1253).
        AttachJournal();
        bool journalHadRecords = _journal!.LastLsn > 0;

        // LSNs resume above both the journal's last record and the floor the newest header
        // generation persisted: a checkpoint truncates the journal before it appends its
        // own record, and when that record is lost the journal alone would restart LSNs
        // below the ones data pages already carry (#1242). The redo point below rests on it.
        _journal.RaiseLsnFloor(header.LsnFloor);

        var recovery = StorageRecovery.Run(Data, _journal, RequiresDurableFlush, protectedPages, _bufferPool.Capacity);

        // Sequence assignment resumes above both the journal's highest observed
        // sequence and the header floor persisted at the last checkpoint — the
        // journal alone is insufficient because checkpoints truncate it while row
        // version stamps persist in data pages.
        _nextTransactionSequence = Math.Max(recovery.MaxSequence, header.SequenceFloor);

        // The redo point: every LSN a page carries from before the journal's checkpoint is at or
        // below that checkpoint's record, and below the LSN floor when the record was lost after
        // the truncation, so a page above it has a full page image in the journal (invariant P;
        // recovery stamped every page it rebuilt with the LSN of its last record). Without the
        // floor a lost checkpoint record would leave the redo point at zero, no page would be
        // imaged again, and the next delta of a page would have no image to chain onto. The page
        // scan below can raise it further.
        long redoLsn = Math.Max(header.LsnFloor, recovery.CheckpointLsn);

        // Rebuild the free-space map and the per-owner page directory in one pass
        // over the on-disk page headers. The stream length is the source of truth
        // for the page count (the file header trails it if the process stopped
        // between an allocation and the next header update). Page headers are also
        // the single source of truth for chain membership — there is no persisted
        // directory to drift from reality (the free-space-map precedent).
        _freeSpaceMap.MarkAllocated((PageId)0L);

        long pageCount = Data.Length / Page.Size;
        var pageHeader = new byte[Page.HeaderSize];
        byte[]? pageBuffer = null;
        long strayLsn = 0;

        for (long i = 1; i < pageCount; i++)
        {
            Data.ReadPageHeader((PageId)i, pageHeader);

            PageType type;
            ulong pageOwner;
            long pageLsn;
            fixed (byte* headerPtr = pageHeader)
            {
                type = ((Page.Header*)headerPtr)->Type;
                pageOwner = ((Page.Header*)headerPtr)->OwnerId;
                pageLsn = ((Page.Header*)headerPtr)->Lsn;
            }

            // An anchor page off the newest generation's chain is the other slot's (whose
            // generation the next header write replaces) or a leftover of a crashed header
            // write: free. Its on-disk bytes are never read again — allocation overwrites
            // without reading.
            if (type == PageType.Free || (type == PageType.CheckpointAnchor && !protectedPages.Contains(i)))
            {
                _freeSpaceMap.MarkFree((PageId)i);
            }
            else
            {
                _freeSpaceMap.MarkAllocated((PageId)i);

                if (type == PageType.Data)
                {
                    // Ascending scan: the last page seen per owner becomes that
                    // owner's current write page.
                    RegisterOwnerPage(pageOwner, (PageId)i);
                }

                // A page recovery did not rebuild has no image in the journal, so invariant P
                // needs its LSN at or below the redo point. One above it outlived the journal
                // records that stamped it: under CommitDurability.None the write-ahead gate only
                // drains the journal, so a power loss can keep a stolen page and lose the journal
                // tail its image and deltas were in; a journal file lost or restored from an older
                // copy does the same. Its LSN is taken only from a page whose stamped checksum
                // verifies, as every write-back leaves it: a damaged header must not move LSNs (the
                // page fails its checksum when it is read), and neither may a page whose checksum
                // field reads zero, which is never verified.
                if (pageLsn > redoLsn && pageLsn > strayLsn && !recovery.RebuiltPages.Contains(i))
                {
                    pageBuffer ??= new byte[Page.Size];
                    Data.ReadPage((PageId)i, pageBuffer);
                    if (PageChecksum.TryVerify(pageBuffer, out uint stored, out _) && stored != 0)
                    {
                        strayLsn = pageLsn;
                    }
                }
            }
        }

        if (strayLsn > redoLsn)
        {
            // LSNs resume above the stray page, so they never fall below one a page carries, and the
            // redo point moves up to it: the page's next first touch journals its full image, which
            // its later deltas chain onto. Without this the next LSNs restarted below the page's,
            // no image was journaled for it (its LSN was above the redo point), and its next delta
            // named a base no record in the journal produced — the following open refused the file
            // set with a chain gap (#1253 review). Nothing has been appended since open, which
            // RaiseLsnFloor requires.
            _journal.RaiseLsnFloor(strayLsn);
            redoLsn = strayLsn;
        }

        Volatile.Write(ref _redoLsn, redoLsn);

        _pageManager = new StoragePageManager(Data, _bufferPool, _freeSpaceMap) { WroteOutsideJournal = ForgetShadow };

        // Everything the journal described is now in the data file; start it clean.
        if (journalHadRecords && checkpointOnOpen)
        {
            Checkpoint();
        }

        _openedAt = (_journal.LastLsn, _nextTransactionSequence);

        // A deferred open-time checkpoint is the owner's to take (an engine's CompleteRecovery);
        // the time backstop counts from the open either way.
        Volatile.Write(ref _lastCheckpointTimestamp, System.Diagnostics.Stopwatch.GetTimestamp());
        if (!journalHadRecords || checkpointOnOpen)
        {
            Volatile.Write(ref _lastCheckpointLsn, _journal.LastLsn);
        }
    }

    /// <summary>
    /// Begins a storage-level transaction: the unit of atomicity and durability for
    /// record mutations. See <see cref="StorageTransaction"/> for the semantics.
    /// </summary>
    /// <returns>The new transaction scope.</returns>
    public StorageTransaction BeginTransaction()
    {
        if (_journal is null)
        {
            throw new InvalidOperationException("Storage has not been initialized.");
        }

        ThrowIfOffline();
        long sequence;
        lock (_transactionLock)
        {
            sequence = ++_nextTransactionSequence;

            // Counted under the same lock the checkpoint holds, so a checkpoint can
            // never truncate the journal between a transaction's begin and its
            // completion (which would discard the full page images its changes chain onto).
            _activeTransactionCount++;
        }

        try
        {
            _journal.AppendBegin(sequence);
        }
        catch
        {
            // No transaction exists for the caller to complete, so nothing else would
            // return the count, and every later checkpoint would refuse to run.
            lock (_transactionLock)
            {
                _activeTransactionCount--;
            }

            throw;
        }

        return new StorageTransaction(this, sequence);
    }

    /// <summary>
    /// Reserves the next transaction sequence from this storage's monotonic
    /// sequence space without beginning a transaction. This is the seam an
    /// MVCC transaction manager layered above the storage uses as its sequence
    /// allocator, so logical (manager) and physical (storage) transactions share
    /// one sequence namespace in the journal — a prerequisite for recovery
    /// classification to be collision-free.
    /// </summary>
    /// <returns>The reserved sequence, unique within this storage instance.</returns>
    public long ReserveTransactionSequence()
    {
        if (_journal is null)
        {
            throw new InvalidOperationException("Storage has not been initialized.");
        }

        ThrowIfOffline();
        lock (_transactionLock)
        {
            return ++_nextTransactionSequence;
        }
    }

    /// <summary>
    /// Begins a storage-level transaction that adopts a sequence previously
    /// obtained from <see cref="ReserveTransactionSequence"/> — the physical
    /// write-ahead bracket paired with a logical transaction that owns the same
    /// sequence. Unlike <see cref="BeginTransaction()"/>, no begin record is
    /// appended: the reserving caller's transaction log owns the lifecycle
    /// records; the adopted bracket contributes page images and its commit or
    /// rollback record under the shared sequence.
    /// </summary>
    /// <param name="sequence">The reserved sequence the transaction runs under.</param>
    /// <returns>The new transaction scope.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sequence"/> is not positive.</exception>
    public StorageTransaction BeginTransaction(long sequence)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sequence);

        if (_journal is null)
        {
            throw new InvalidOperationException("Storage has not been initialized.");
        }

        ThrowIfOffline();
        lock (_transactionLock)
        {
            // Adopted sequences normally come from ReserveTransactionSequence, but
            // keep the counter monotonic even for a caller that minted its own —
            // a later internally assigned sequence must never collide.
            if (sequence > _nextTransactionSequence)
            {
                _nextTransactionSequence = sequence;
            }

            _activeTransactionCount++;
        }

        // Deliberately no begin record: the reserving caller's transaction log owns
        // the lifecycle records (see the summary above); page images
        // journaled by this bracket carry the sequence, which is all recovery needs.
        return new StorageTransaction(this, sequence);
    }

    /// <summary>
    /// Pins a page for modification inside a transaction: acquires the page's write
    /// lock for the transaction and captures its pre-image on first touch, so the
    /// mutation is covered by the write-ahead log like any record operation. Used by
    /// subsystems that own their page layout (index structures, catalogs).
    /// </summary>
    /// <param name="transaction">The owning storage transaction.</param>
    /// <param name="pageId">The page to modify.</param>
    /// <returns>A handle to the pinned page; the caller marks it dirty after mutating.</returns>
    /// <exception cref="StorageTransactionException">The transaction is not active, or the page is owned by another transaction.</exception>
    /// <exception cref="StorageIOException">The page is not allocated, or it is page 0, the file header, which is never a data page.</exception>
    public StoragePageHandle OpenPageForWrite(StorageTransaction transaction, PageId pageId)
    {
        var owner = ValidateTransaction(transaction);
        return TouchPage(owner, pageId);
    }

    /// <summary>
    /// Allocates a fresh page inside a transaction, covered by the write-ahead log.
    /// If the transaction rolls back, the page content reverts to its freshly
    /// allocated (empty) image; the allocation itself is not undone — a safe leak.
    /// </summary>
    /// <param name="transaction">The owning storage transaction.</param>
    /// <param name="type">The type of page to allocate.</param>
    /// <returns>A handle to the new pinned page.</returns>
    /// <exception cref="StorageTransactionException">The transaction is not active.</exception>
    public StoragePageHandle AllocatePageForWrite(StorageTransaction transaction, PageType type)
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

    /// <summary>
    /// Checkpoints the storage: flushes all page state to the data stream using the
    /// storage's durability policy and truncates the journal, so the next open
    /// recovers instantly. Non-durable storage does not promise crash persistence.
    /// </summary>
    /// <exception cref="StorageTransactionException">A transaction is still active.</exception>
    public void Checkpoint() => Checkpoint(ReadOnlySpan<long>.Empty);

    /// <summary>
    /// Checkpoints the storage while logical (manager-level) transactions are in
    /// flight above it: the truncating checkpoint record carries the given
    /// sequences, so recovery classification still sees them even though their
    /// begin records were truncated. Storage-level transactions must still be
    /// quiescent — the active-count interlock is unchanged. The sequences are also
    /// recorded in the file header before the truncation
    /// (<see cref="CheckpointActiveTransactions"/>), so they stay classified when the
    /// checkpoint record itself is lost; the header holds any number of them.
    /// </summary>
    /// <param name="activeTransactionSequences">
    /// The logical transactions in flight whose row versions the truncation must leave
    /// classifiable: the transaction layer passes its writers (transactions that can have
    /// stamped versions or still owe an undo). A reader stamps nothing and needs no entry.
    /// </param>
    /// <remarks>
    /// <para>
    /// The order is PostgreSQL's (<c>CreateCheckPoint</c> in
    /// <c>src/backend/access/transam/xlog.c</c>: the data flush, then the control file, then
    /// WAL recycling), with the header carrying what the journal is about to lose:
    /// </para>
    /// <list type="number">
    /// <item><description>The checkpoint anchor's overflow pages of the slot being written, then every dirty page, then a durable data flush.</description></item>
    /// <item><description>A new header generation into the other slot of page 0 — the sequence floor, the LSN floor (the journal's last LSN) and the anchor — then a durable data flush.</description></item>
    /// <item><description>The journal truncation and its checkpoint record.</description></item>
    /// </list>
    /// <para>
    /// So a checkpoint record lost after the truncation loses nothing: the anchor still
    /// classifies the transactions it lists (<see cref="CheckpointActiveTransactions"/>), and
    /// the LSN floor keeps the next LSN above every LSN a data page carries.
    /// </para>
    /// </remarks>
    /// <exception cref="StorageTransactionException">
    /// A storage-level transaction is still active: the checkpoint changed nothing, and a
    /// later one can succeed.
    /// </exception>
    /// <exception cref="StorageOfflineException">
    /// The storage is offline, or a durable flush this checkpoint made, or its header slot write,
    /// failed and took it offline.
    /// </exception>
    public void Checkpoint(ReadOnlySpan<long> activeTransactionSequences)
    {
        // The whole checkpoint runs under the transaction lock: BeginTransaction
        // increments the active count under the same lock, so no transaction can
        // start (and journal no page image) between the emptiness check and the
        // journal truncation. Lock order is transaction lock → header → buffer pool →
        // journal, and transaction lock → owner lock → free-space map for commit-time
        // frees; no other path takes them in the opposite order.
        lock (_transactionLock)
        {
            ThrowIfOffline();

            if (_activeTransactionCount > 0)
            {
                throw new StorageTransactionException("Checkpoint requires no active transactions.");
            }

            // The anchor and both floors reach durable storage before the journal
            // truncation destroys the records they stand in for.
            WriteHeader(activeTransactionSequences);

            // The debug consistency check compares every page it shadows with what recovery would
            // rebuild from the records the truncation is about to discard.
            _consistency?.Checkpointing(ReadPageForAudit);

            // Read once: a CommitDurability change racing the checkpoint must not make it flush
            // without an fsync and then publish that LSN to the gate as durable. The setter takes
            // this checkpoint's transaction lock too (owner decision 26 of 2026-10-06), so the
            // header write above, its page write-backs and this flush all saw the same value. And
            // an initialized storage never enters or leaves None, so a commit waiting durably in
            // the gate never meets a truncation this checkpoint made without flushing the data file.
            bool durable = RequiresDurableFlush;
            long? checkpointLsn = _journal?.Checkpoint(activeTransactionSequences, forceDurable: durable);

            if (checkpointLsn is not null)
            {
                // Every page now carries an LSN below the checkpoint record's, and the journal holds
                // no record of any page: each one's next change journals a full page image first.
                // Under the transaction lock with no transaction active, so no transaction sees the
                // redo point move.
                Volatile.Write(ref _redoLsn, checkpointLsn.Value);

                if (durable)
                {
                    // Wake any group-commit bookkeeping past the truncation point. The journal's own
                    // durable LSN, not the checkpoint's: the gate publishes only what an fsync confirmed.
                    _groupCommitGate.PublishDurable(_journal!.DurableLsn);
                }
            }

            MarkCheckpointed();
        }
    }

    /// <summary>
    /// Performs one group-commit flush pass on behalf of a write-ahead flush worker:
    /// makes the journal durable up to the highest commit currently waiting on the
    /// grouped durability gate and wakes every covered committer. A no-op when
    /// nothing is pending (including in the synchronous durability mode).
    /// </summary>
    /// <returns>True when a durable flush was performed; false when nothing was pending.</returns>
    /// <remarks>An offline storage flushes nothing and returns false.</remarks>
    public bool FlushPendingCommits()
    {
        if (_journal is null || !RequiresDurableFlush || IsOffline)
        {
            return false;
        }

        try
        {
            return _groupCommitGate.FlushPending(_journal);
        }
        catch (StorageOfflineException)
        {
            // This flush took the storage offline. The committers waiting on it are released
            // (the journal abandoned the gate) and each gets the error from its own flush.
            return false;
        }
    }

    /// <summary>
    /// Writes back up to <paramref name="maxPages"/> dirty buffered pages to the data
    /// stream — the paced write-back a page-writer worker performs between
    /// checkpoints so a checkpoint's flush does not spike. Honors the write-ahead
    /// rule: durable storage makes the journal durable past each page's LSN before
    /// the page is written; non-durable storage flushes it ordinarily first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pinned dirty pages are skipped: a pin is how a writer changes a page, so only an
    /// unpinned page is written as the complete image its last writer left. A skipped page
    /// stays dirty until a later pass, an eviction, or a checkpoint writes it. The return
    /// value can therefore be smaller than <paramref name="maxPages"/> while dirty pages
    /// remain, and a return of zero does not mean the pool is clean.
    /// </para>
    /// <para>An offline storage writes nothing and returns zero.</para>
    /// </remarks>
    /// <param name="maxPages">The maximum number of dirty pages to write in this pass.</param>
    /// <returns>The number of pages written.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxPages"/> is not positive.</exception>
    public int WriteBackDirtyPages(int maxPages)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPages);

        if (_pageManager is null || _disposed || IsOffline)
        {
            return 0;
        }

        return _bufferPool.FlushSome(Data, maxPages);
    }

    /// <summary>
    /// Inserts a record into the shared (owner-zero) record space within a storage
    /// transaction. Automatically allocates a new data page if the current page is full.
    /// </summary>
    /// <param name="transaction">The owning storage transaction.</param>
    /// <param name="data">The record data to insert.</param>
    /// <returns>The page identifier and slot index where the record was stored.</returns>
    /// <exception cref="SlottedPageException">The record is larger than a single page can hold.</exception>
    /// <exception cref="StorageTransactionException">The transaction is not active, or the target page is owned by another transaction.</exception>
    protected (PageId PageId, int SlotIndex) InsertRecord(StorageTransaction transaction, ReadOnlySpan<byte> data)
        => InsertRecord(transaction, 0, data);

    /// <summary>
    /// Inserts a record into the specified owner's record chain within a storage
    /// transaction: the record lands on the owner's current write page (a new page
    /// is allocated and tagged with the owner when needed), so scans scoped to the
    /// owner touch only its own pages.
    /// </summary>
    /// <param name="transaction">The owning storage transaction.</param>
    /// <param name="ownerId">The owner whose chain receives the record; zero is the shared space.</param>
    /// <param name="data">The record data to insert.</param>
    /// <returns>The page identifier and slot index where the record was stored.</returns>
    /// <exception cref="SlottedPageException">The record is larger than a single page can hold.</exception>
    /// <exception cref="StorageTransactionException">The transaction is not active, or the target page is owned by another transaction.</exception>
    protected unsafe (PageId PageId, int SlotIndex) InsertRecord(StorageTransaction transaction, ulong ownerId, ReadOnlySpan<byte> data)
    {
        var owner = ValidateTransaction(transaction);

        if (data.Length > SlottedPage.MaxRecordSize)
        {
            throw new SlottedPageException(
                $"Record of {data.Length} bytes exceeds the maximum record size of {SlottedPage.MaxRecordSize} bytes.");
        }

        StoragePageHandle handle;
        SlottedPage slotted;

        PageId? currentWritePage;
        lock (_ownerLock)
        {
            currentWritePage = _currentWritePages.TryGetValue(ownerId, out var current) ? current : null;
        }

        // The current write page may have been freed since the last insert.
        if (currentWritePage is null || !_freeSpaceMap.IsAllocated(currentWritePage.Value))
        {
            handle = AllocateDataPage(owner, ownerId, out slotted);
        }
        else
        {
            handle = TouchPage(owner, currentWritePage.Value);
            slotted = new SlottedPage(handle.Page);

            if (handle.Page.Type != PageType.Data || handle.Page.OwnerId != ownerId || !slotted.CanFit(data.Length))
            {
                handle.Dispose();
                handle = AllocateDataPage(owner, ownerId, out slotted);
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
    /// Deletes a record within a storage transaction and releases its page when no live slots remain.
    /// </summary>
    /// <param name="transaction">The owning storage transaction.</param>
    /// <param name="pageId">The page containing the record.</param>
    /// <param name="slotIndex">The slot index within the page.</param>
    /// <exception cref="StorageTransactionException">The transaction is not active, or the target page is owned by another transaction.</exception>
    protected unsafe void DeleteRecord(StorageTransaction transaction, PageId pageId, int slotIndex)
    {
        var owner = ValidateTransaction(transaction);

        using var handle = TouchPage(owner, pageId);
        var slotted = new SlottedPage(handle.Page);
        slotted.DeleteSlot(slotIndex);
        handle.MarkDirty();

        for (int index = 0; index < slotted.SlotCount; index++)
        {
            if (slotted.GetSlotLength(index) != 0)
            {
                return;
            }
        }

        // Reclaim through the same commit-time free path as FreeOwnerPages.
        // Until commit the allocator cannot reuse the page, and rollback restores
        // the complete pre-image and leaves its owner directory intact.
        var page = handle.Page;
        ulong pageOwner = page.OwnerId;
        ClearBody(page);
        page.Type = PageType.Free;
        page.OwnerId = 0;
        slotted.Initialize();
        owner.RegisterPendingFree((long)pageId, pageOwner);
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
    protected unsafe void UpdateRecord(StorageTransaction transaction, PageId pageId, int slotIndex, ReadOnlySpan<byte> data)
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
    /// Flushes all dirty pages to the underlying data stream, flushes the journal
    /// according to the selected durability policy, and updates the file header.
    /// </summary>
    protected void Flush()
    {
        ThrowIfOffline();
        WriteHeader(Volatile.Read(ref _checkpointActives));
        _journal?.Flush(forceDurable: RequiresDurableFlush);
    }

    /// <summary>
    /// Gets an iterator for scanning all storage units (records) across data pages.
    /// </summary>
    /// <remarks>
    /// Best for performing raw full-table scans through the entire storage resource.
    /// </remarks>
    /// <returns>A new storage unit iterator.</returns>
    public StorageUnitIterator GetUnitIterator()
    {
        return new StorageUnitIterator(_pageManager!, _freeSpaceMap);
    }

    /// <summary>
    /// Gets an iterator scoped to one owner's record chain: only data pages tagged
    /// with <paramref name="ownerId"/> are visited, so a scan of one object touches
    /// O(object) pages instead of O(storage). Owner zero iterates the shared,
    /// untagged record space.
    /// </summary>
    /// <param name="ownerId">The owner whose pages to scan.</param>
    /// <returns>A new storage unit iterator over the owner's pages.</returns>
    public StorageUnitIterator GetUnitIterator(ulong ownerId)
    {
        return new StorageUnitIterator(_pageManager!, _freeSpaceMap, SnapshotOwnerPages(ownerId), ownerId);
    }

    /// <summary>
    /// Gets a point-in-time snapshot of the data pages currently belonging to the
    /// specified owner's record chain, in ascending page order.
    /// </summary>
    /// <param name="ownerId">The owner whose pages to list.</param>
    /// <returns>The owner's data pages; empty when the owner holds none.</returns>
    public IReadOnlyList<PageId> GetOwnerPages(ulong ownerId)
    {
        long[] pages = SnapshotOwnerPages(ownerId);
        var result = new PageId[pages.Length];

        for (int i = 0; i < pages.Length; i++)
        {
            result[i] = (PageId)pages[i];
        }

        return result;
    }

    /// <summary>
    /// Releases every data page of the specified owner's record chain inside a
    /// transaction: each page is retyped <see cref="PageType.Free"/> under the
    /// write-ahead log (a rollback restores the chain from the pre-image), and
    /// the pages return to the free-space map when the transaction commits, never
    /// before, so an in-flight release can never be reallocated.
    /// </summary>
    /// <param name="transaction">The owning storage transaction.</param>
    /// <param name="ownerId">The owner whose chain to release.</param>
    /// <returns>The number of pages released.</returns>
    /// <exception cref="StorageTransactionException">The transaction is not active, or a chain page is owned by another transaction.</exception>
    public unsafe int FreeOwnerPages(StorageTransaction transaction, ulong ownerId)
    {
        var owner = ValidateTransaction(transaction);
        long[] pages = SnapshotOwnerPages(ownerId);

        foreach (long pageId in pages)
        {
            // Retype under the transaction: the pre-image covers the whole page
            // (records included), so rollback restores the chain and recovery redoes
            // the release only once its transaction committed;
            // a committed release replays as a Free page and the open-time header
            // scan rebuilds both the free-space map and the directory accordingly.
            using var handle = TouchPage(owner, (PageId)pageId);
            var page = handle.Page;
            ClearBody(page);
            page.Type = PageType.Free;
            page.OwnerId = 0;

            var slotted = new SlottedPage(page);
            slotted.Initialize();

            handle.MarkDirty();
            owner.RegisterPendingFree(pageId, ownerId);
        }

        return pages.Length;
    }

    /// <summary>
    /// Records a data page as belonging to an owner's chain and makes it the
    /// owner's current write page.
    /// </summary>
    private void RegisterOwnerPage(ulong ownerId, PageId pageId)
    {
        lock (_ownerLock)
        {
            if (!_ownerPages.TryGetValue(ownerId, out var pages))
            {
                pages = new SortedSet<long>();
                _ownerPages[ownerId] = pages;
            }

            pages.Add((long)pageId);
            _currentWritePages[ownerId] = pageId;
        }
    }

    /// <summary>
    /// Snapshots an owner's page ids in ascending order.
    /// </summary>
    private long[] SnapshotOwnerPages(ulong ownerId)
    {
        lock (_ownerLock)
        {
            if (!_ownerPages.TryGetValue(ownerId, out var pages) || pages.Count == 0)
            {
                return Array.Empty<long>();
            }

            var snapshot = new long[pages.Count];
            pages.CopyTo(snapshot);
            return snapshot;
        }
    }

    /// <summary>
    /// Applies a committed transaction's page releases: the pages return to the
    /// free-space map and leave the owner directory. Deferred to commit so the
    /// allocator can never hand out a page whose release might still roll back.
    /// </summary>
    /// <remarks>
    /// Runs under the transaction lock, after the transaction's page write locks are released
    /// (<see cref="CompleteCommitted"/>). Each page is freed once, however many times the
    /// transaction released it (<see cref="StorageTransaction.RegisterPendingFree"/>): a second
    /// free could put the page back on the free list after another allocation took it.
    /// </remarks>
    private void ApplyPendingFrees(StorageTransaction transaction)
    {
        var pendingFrees = transaction.PendingFrees;

        if (pendingFrees is null)
        {
            return;
        }

        lock (_ownerLock)
        {
            foreach (var (pageId, ownerId) in pendingFrees)
            {
                if (_ownerPages.TryGetValue(ownerId, out var pages))
                {
                    pages.Remove(pageId);

                    if (pages.Count == 0)
                    {
                        _ownerPages.Remove(ownerId);
                    }
                }

                if (_currentWritePages.TryGetValue(ownerId, out var current) && (long)current == pageId)
                {
                    _currentWritePages.Remove(ownerId);
                }

                _freeSpaceMap.Free((PageId)pageId);
            }
        }
    }

    /// <summary>
    /// Commits a storage transaction: appends, for every touched page it changed, the byte runs
    /// that differ from the page's pre-image (a page delta, or a committed full image when the
    /// delta passes half a page and the image is hardly longer), then a commit record, then — unless
    /// the caller owns durability through a later record — applies the selected journal
    /// durability policy before returning (#1253).
    /// </summary>
    /// <remarks>
    /// Each record names the LSN the page carried, the LSN of the page's last record (its full
    /// page image when this transaction journaled one), and the page is then stamped with the new
    /// record's LSN: the chain recovery follows. A page the transaction touched and left unchanged
    /// journals nothing and keeps its LSN.
    /// </remarks>
    internal unsafe void CommitTransaction(StorageTransaction transaction, bool awaitDurability = true)
    {
        // Read once: the setting in effect when the commit starts decides both the check below and
        // the wait after the commit record, so a concurrent change cannot pass the check under one
        // mode and wait under another (owner decision 26 of 2026-10-06; PostgreSQL reads
        // synchronous_commit once per commit, xact.c:1540-1542).
        var durability = CommitDurability;

        // A durable wait the journal cannot provide is refused before anything is journaled
        // (#1018): the bracket stays active and the caller rolls it back, which recovery agrees
        // with because no commit record exists. Refused after the commit record instead, the
        // caller's rollback restored pre-images and their base LSNs that the journal had already
        // moved past, the page's next delta named that old base, and the next open refused the
        // whole file set with a chain gap (#1253 review). Engines never get here:
        // ConfigureCommitDurability refuses a durable mode on such a store first, but the public
        // CommitDurability setter does not, and the journal's handle can change underneath.
        if (awaitDurability && durability != StorageCommitDurability.None && !Journal.SupportsDurableFlush)
        {
            throw new NotSupportedException(
                $"Storage transaction {transaction.Sequence} cannot commit with CommitDurability '{durability}': the journal's backing " +
                "handle does not support a durable flush (SupportsDurableFlush = false). Nothing was journaled; the transaction is still " +
                "active and can be rolled back.");
        }

        // Deterministic page order keeps the journal replayable and testable.
        var pageIds = new List<long>(transaction.PreImages.Keys);
        pageIds.Sort();

        byte[] before = ArrayPool<byte>.Shared.Rent(Page.Size);
        byte[] delta = ArrayPool<byte>.Shared.Rent(PageImageCodec.MaximumPayloadLength);
        byte[]? image = null;
        try
        {
            foreach (long pageId in pageIds)
            {
                using var handle = _pageManager!.GetPage((PageId)pageId);
                var page = handle.Page;
                var current = new ReadOnlySpan<byte>(page.Pointer, Page.Size);
                var preImage = transaction.PreImages[pageId];
                LoadPreImage(preImage, before.AsSpan(0, Page.Size));
                ThrowIfLsnMoved(pageId, page.Lsn, preImage.BaseLsn);

                int runsLength = PageImageCodec.EncodeRuns(before.AsSpan(0, Page.Size), current, delta.AsSpan(PageImageCodec.BaseLsnSize));
                if (runsLength == 0)
                {
                    _consistency?.CommittedUnchanged(pageId, current);
                    continue;
                }

                var type = JournalRecordType.PageDelta;
                var payload = delta;
                if (runsLength > PageImageCodec.CommittedImageThreshold)
                {
                    // Most of the page changed (a rewritten Blob page, a page a delete cleared): its
                    // full image may be shorter than the delta, and needs no base content to apply.
                    image ??= ArrayPool<byte>.Shared.Rent(PageImageCodec.MaximumPayloadLength);
                    int imageLength = PageImageCodec.EncodeImage(current, image.AsSpan(PageImageCodec.BaseLsnSize));
                    if (imageLength <= runsLength + PageImageCodec.CommittedImageSlack)
                    {
                        type = JournalRecordType.CommittedPageImage;
                        payload = image;
                        runsLength = imageLength;
                    }
                }

                // The base: the LSN of the page's last record, which recovery's rebuilt page carries.
                BinaryPrimitives.WriteInt64LittleEndian(payload, preImage.BaseLsn);
                var record = payload.AsSpan(0, PageImageCodec.BaseLsnSize + runsLength);
                long lsn = _journal!.AppendPageRecord(transaction.Sequence, (PageId)pageId, type, record, out _);
                _consistency?.CommitRecordJournaled(transaction.Sequence, pageId, type, lsn, record, current);

                page.Lsn = lsn;
                handle.MarkDirty();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(before);
            ArrayPool<byte>.Shared.Return(delta);
            if (image is not null)
            {
                ArrayPool<byte>.Shared.Return(image);
            }
        }

        long commitLsn = _journal!.AppendCommit(transaction.Sequence);
        transaction.CommitRecordLsn = commitLsn;
        _consistency?.CommitRecordAppended(transaction.Sequence);

        try
        {
            if (awaitDurability)
            {
                // Durability only controls the flush after the same commit record.
                // An outer logical commit may own this wait through its later record.
                AwaitCommitDurable(commitLsn, durability);
            }
        }
        catch (StorageOfflineException offline)
        {
            // The commit record is in the journal and its flush failed, which took the storage
            // offline: nothing of this bracket is written again, and the reopen's recovery
            // decides its outcome. The bracket ends committed in memory, as recovery reads it
            // whenever the record reached stable storage; rolling its pages back would show this
            // process the opposite (#1243). The exception says the record was written, so an
            // engine reports the operation as unconfirmed, not refused: a self-committing
            // statement whose bracket this was survives the reopen when the record did.
            CompleteCommitted(transaction);
            throw StorageOfflineException.CommitUnconfirmed(offline);
        }
        catch
        {
            // Any other failure of the wait (the storage disposed under it, a durable flush the
            // handle stopped supporting after the check above): the commit record is in the journal
            // all the same, so recovery redoes the bracket whenever the record reaches the media.
            // The bracket must not roll back in memory — its pages would return to bases the journal
            // has moved past — so it ends committed, unconfirmed, and the failure propagates.
            CompleteCommitted(transaction);
            throw;
        }

        CompleteCommitted(transaction);
    }

    /// <summary>
    /// Ends a bracket whose commit record is in the journal: its page write locks and its place in
    /// the active count are released, then its page releases take effect (the freed pages re-enter
    /// the allocator and leave their owner chains), in one hold of the transaction lock.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every page write lock is taken under the transaction lock (<see cref="RegisterTouch"/>), so
    /// dropping the bracket's locks before its pages reach the free-space map, inside that one
    /// hold, means no page is ever on the free list while this bracket write-locks it. Another
    /// transaction may take a freed page from the map the moment it is there: its touch of the
    /// page waits for the transaction lock, and then finds the page unlocked. The two steps used
    /// to run the other way round under separate locks, and an allocation that took a freed page
    /// between them failed with "write-locked by" this bracket (the #1157 concurrency test on CI
    /// runners with fewer cores than the test has threads).
    /// </para>
    /// <para>
    /// PostgreSQL meets the same hazard on the allocating side: an index page the free-space map
    /// reports is used only if its buffer lock can be taken without waiting, because a page someone
    /// holds is not really free (<c>_bt_allocbuf</c>, <c>src/backend/access/nbtree/nbtpage.c:862-951</c>),
    /// and VACUUM records a deleted page in the map only once no transaction can still reach it
    /// (<c>_bt_pendingfsm_finalize</c>, <c>nbtpage.c:3013-3068</c>). Here a page reaches the map only
    /// after its last holder let go of it, so an allocation never has to skip one.
    /// </para>
    /// </remarks>
    private void CompleteCommitted(StorageTransaction transaction)
        => ReleasePageWriteLocks(transaction, applyPendingFrees: true);

    /// <summary>
    /// Restores a page's pre-image into <paramref name="destination"/>: the content outside the LSN
    /// and checksum fields, and the LSN of the page's last record. A spilled pre-image is read back
    /// from its full page image record in the journal.
    /// </summary>
    /// <param name="preImage">The pre-image.</param>
    /// <param name="destination">A page, <see cref="Page.Size"/> bytes.</param>
    private void LoadPreImage(in StoragePreImage preImage, Span<byte> destination)
    {
        string? problem;
        if (preImage.Runs is { } runs)
        {
            problem = PageImageCodec.TryApplyImage(runs, destination);
        }
        else
        {
            byte[] payload = ArrayPool<byte>.Shared.Rent(PageImageCodec.MaximumPayloadLength);
            try
            {
                int length = _journal!.ReadPageRecord(preImage.Location, payload);
                problem = PageImageCodec.TryApplyImage(payload.AsSpan(0, length), destination);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(payload);
            }
        }

        if (problem is not null)
        {
            // The runs were encoded by this process (or verified by their frame's checksum when
            // read back), so this is a defect, not damage on disk.
            throw new InvalidOperationException($"A storage transaction's pre-image does not decode: {problem}.");
        }

        BinaryPrimitives.WriteInt64LittleEndian(destination[Page.LsnFieldOffset..], preImage.BaseLsn);
    }

    /// <summary>
    /// Refuses to journal a page change whose page no longer carries the LSN its transaction found:
    /// only the transaction's own first touch stamps a page's LSN until it completes, so another
    /// LSN means the page was changed outside the storage transaction, and a delta naming either
    /// LSN would leave recovery a page it cannot rebuild.
    /// </summary>
    private static void ThrowIfLsnMoved(long pageId, long pageLsn, long baseLsn)
    {
        if (pageLsn != baseLsn)
        {
            throw new InvalidOperationException(
                $"Page {pageId} carries LSN {pageLsn}, but its storage transaction found it at LSN {baseLsn}: the page was changed outside " +
                "the transaction, and its changes cannot be journaled.");
        }
    }

    /// <summary>
    /// Tells the debug consistency check that a page was rewritten outside the journal on purpose
    /// (the page manager's allocation clear and its raw <see cref="StoragePageManager.FreePage"/>).
    /// </summary>
    private void ForgetShadow(long pageId) => _consistency?.Forget(pageId);

    /// <summary>
    /// Reads the data file's copy of a page for the consistency check's audit of an imaging touch;
    /// false when the file does not reach the page.
    /// </summary>
    private bool ReadStoredPage(long pageId, byte[] buffer) => _bufferPool.TryReadStored((PageId)pageId, Data, buffer);

    /// <summary>
    /// Reads a page as the storage holds it now, for the consistency check's checkpoint audit: the
    /// buffer pool's copy when the page is resident, otherwise the data file's.
    /// </summary>
    private unsafe void ReadPageForAudit(long pageId, byte[] buffer)
    {
        if (_bufferPool.TryGet((PageId)pageId, out var handle) && handle is not null)
        {
            using (handle)
            {
                new ReadOnlySpan<byte>(handle.Page.Pointer, Page.Size).CopyTo(buffer);
            }

            return;
        }

        if (!ReadStoredPage(pageId, buffer))
        {
            // A shadowed page past the end of the data file: its records describe a page the file
            // never reached, which is an inconsistency the compare reports against zeros.
            Array.Clear(buffer);
        }
    }

    /// <summary>
    /// Rolls a storage transaction back: restores every touched page to its pre-image
    /// in the buffer pool and appends a rollback record.
    /// </summary>
    /// <remarks>
    /// The rollback record is advisory: recovery redoes no change of a transaction without
    /// a commit record. Once the pages are restored the transaction therefore ends even
    /// when the record cannot be appended: its page write locks and its place in the
    /// active count are released before the append failure propagates, so a journal
    /// failure cannot leave the storage refusing every later checkpoint. A failure while
    /// restoring the pages leaves the transaction active, so the caller can retry.
    /// </remarks>
    internal unsafe void RollbackTransaction(StorageTransaction transaction)
    {
        if (transaction.CommitRecordLsn > 0)
        {
            // A defect, whatever the check mode: a bracket whose commit record is in the journal
            // always ends committed (CommitTransaction), because recovery redoes it. Rolling it back
            // would restore bases its deltas moved past and break the pages' chains (#1018).
            throw new InvalidOperationException(
                $"Storage transaction {transaction.Sequence} cannot roll back: its commit record (LSN {transaction.CommitRecordLsn}) is in " +
                "the journal, so recovery redoes its changes.");
        }

        foreach (var (pageId, preImage) in transaction.PreImages)
        {
            using var handle = _pageManager!.GetPage((PageId)pageId);
            var page = new Span<byte>(handle.Page.Pointer, Page.Size);

            // The content and the LSN of the page's last record together: the page as recovery
            // rebuilds it, so the next transaction's delta names the right base.
            LoadPreImage(preImage, page);
            handle.MarkDirty();
            _consistency?.RolledBack(pageId, page);
        }

        // A commit that failed before its commit record may have journaled page records: recovery
        // applies none of them.
        _consistency?.Abandon(transaction.Sequence);

        try
        {
            _journal!.AppendRollback(transaction.Sequence);
        }
        finally
        {
            ReleasePageWriteLocks(transaction);
        }
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
    /// (so the next open recovers instantly), otherwise flushes according to the
    /// selected durability policy. The write-ahead gate has kept the journal ahead
    /// of any stolen page, so recovery rebuilds every page an abandoned transaction
    /// changed from the full page image it journaled first. Non-durable mode makes no promise that those bytes survive.
    /// </summary>
    /// <remarks>
    /// An opened file set that nothing was written through writes nothing on
    /// shutdown: the files stay byte-identical, and a journal whose open-time
    /// checkpoint the owner deferred (to analyze it first) is not truncated
    /// unanalyzed — closing then is equivalent to a crash right after recovery,
    /// which the next open already handles. That is what lets an engine refuse a
    /// database at open (an unsupported format, say) without writing to it on the
    /// way out; the open's own recovery has still run.
    /// </remarks>
    private void ShutdownFlush()
    {
        if (_pageManager is null || _journal is null)
        {
            return;
        }

        if (IsOffline)
        {
            // A durable flush (#1243), a drain of the journal's append buffer (#1252) or a header
            // slot write (#1268) failed: nothing may be written after it, and the records still in
            // the buffer are never written. The journal on the media is what the next open's
            // recovery reads; it decides every unconfirmed commit, and pages left dirty in the pool
            // are rebuilt from it or were never committed.
            return;
        }

        if (IsUnwrittenSinceOpen())
        {
            return;
        }

        bool idle;
        lock (_transactionLock)
        {
            idle = _activeTransactionCount == 0;
        }

        if (idle)
        {
            Checkpoint();
        }
        else
        {
            // Not a checkpoint: the journal keeps its records, and the new header generation
            // carries the anchor the last checkpoint wrote, so it still matches the journal's
            // truncation point.
            WriteHeader(Volatile.Read(ref _checkpointActives));
            _journal.Flush(forceDurable: RequiresDurableFlush);
        }
    }

    /// <summary>
    /// Whether nothing has been written through this opened file set: every
    /// transaction appends its begin record and every checkpoint its checkpoint
    /// record, so an unchanged journal position, an unchanged sequence counter (no
    /// reservation either) and no active transaction mean no page was dirtied and
    /// no header field moved since recovery finished.
    /// </summary>
    private bool IsUnwrittenSinceOpen()
    {
        if (_openedAt is not { } openedAt)
        {
            return false;
        }

        lock (_transactionLock)
        {
            if (_activeTransactionCount > 0 || _nextTransactionSequence != openedAt.Sequence)
            {
                return false;
            }
        }

        return _journal!.LastLsn == openedAt.Lsn;
    }

    private bool RequiresDurableFlush => CommitDurability != StorageCommitDurability.None;

    /// <summary>
    /// Throws <see cref="StorageOfflineException"/> when the storage is offline (see
    /// <see cref="OfflineError"/>).
    /// </summary>
    private void ThrowIfOffline()
    {
        if (OfflineError is { } offline)
        {
            throw StorageOfflineException.Refusal(offline);
        }
    }

    /// <summary>
    /// Takes the storage offline after a failed durable flush of its data file: the journal is
    /// latched too, so nothing more is appended, and the group-commit waiters are released.
    /// PostgreSQL panics on a failed data-file fsync unless <c>data_sync_retry</c> is on, because
    /// the write-back may have been dropped while the pages left the buffer pool clean, and a
    /// later fsync "might falsely report success" (<c>data_sync_elevel</c>,
    /// <c>src/backend/storage/file/fd.c:3966-3987</c>); a later checkpoint here would then
    /// truncate the journal over the lost pages.
    /// </summary>
    /// <param name="cause">The failed flush.</param>
    /// <returns>The exception the caller throws.</returns>
    private StorageOfflineException TakeOffline(Exception cause)
        => GoOffline(StorageOfflineException.Create(StorageOfflineCause.DataFlush, cause));

    /// <summary>
    /// Takes the storage offline with <paramref name="offline"/>, unless it already is (see
    /// <see cref="Latch"/>).
    /// </summary>
    /// <param name="offline">The error that takes the storage offline.</param>
    /// <returns>
    /// <paramref name="offline"/> when it took the storage offline; otherwise the refusal of the
    /// error that did, so a failure that lost a race reports the error <see cref="OfflineError"/>
    /// and <see cref="OnOffline"/> carry, not its own.
    /// </returns>
    private StorageOfflineException GoOffline(StorageOfflineException offline)
    {
        Latch(offline);
        var first = OfflineError ?? offline;
        return ReferenceEquals(first, offline) ? offline : StorageOfflineException.Refusal(first);
    }

    /// <summary>
    /// Takes the storage offline with <paramref name="error"/>, unless it already is. With a journal
    /// attached, the journal's latch is the storage's: it keeps the first error whichever path set
    /// it (its own failed drain or fsync included), releases the group-commit waiters
    /// (<see cref="StorageJournal.WentOffline"/>) and raises <see cref="OnOffline"/> once with that
    /// first error, on this thread when no other raised it yet. Before #1268's review the storage
    /// kept a latch of its own beside the journal's and <see cref="OfflineError"/> read it first, so
    /// a header slot write or data-file fsync that failed after a drain had already taken the
    /// journal offline replaced the error <see cref="OnOffline"/> had been raised with.
    /// </summary>
    /// <param name="error">The error that takes the storage offline.</param>
    private void Latch(StorageOfflineException error)
    {
        if (_journal is { } journal)
        {
            // Idempotent: a journal already offline keeps its error and raises at most once.
            journal.TakeOffline(error);
        }
        else if (Interlocked.CompareExchange(ref _offline, error, null) is null)
        {
            _groupCommitGate.Abandon();
            RaiseOffline(error);
        }
    }

    /// <summary>
    /// Flushes the data file by the durability policy. A durable flush that fails takes the
    /// storage offline (<see cref="TakeOffline"/>) and throws <see cref="StorageOfflineException"/>.
    /// </summary>
    private void FlushData()
    {
        if (!RequiresDurableFlush)
        {
            Data.Flush(durable: false);
            return;
        }

        try
        {
            Data.Flush(durable: true);
        }
        catch (Exception exception) when (exception is not (StorageOfflineException or ObjectDisposedException or NotSupportedException))
        {
            throw TakeOffline(exception);
        }
    }

    /// <summary>
    /// The write-ahead gate: before a page whose LSN is <paramref name="lsn"/> reaches the data
    /// file, the journal holds every record up to it — durably when the policy flushes durably,
    /// and at least out of the append buffer otherwise (#1252), so the full page image a
    /// stolen page is rebuilt from is never still in the process when the page is on the file.
    /// </summary>
    private void FlushWriteAhead(long lsn)
    {
        if (RequiresDurableFlush)
        {
            _journal!.EnsureDurable(lsn);
        }
        else
        {
            // Preserve journal-before-page write ordering without claiming that
            // an ordinary flush makes either memory or buffered bytes durable.
            _journal!.EnsureWritten(lsn);
        }
    }

    private StorageTransaction ValidateTransaction(StorageTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        if (!transaction.IsActive)
        {
            throw new StorageTransactionException($"Storage transaction {transaction.Sequence} has already completed.");
        }

        // A record change would extend the file or dirty a page that can never be written.
        ThrowIfOffline();
        return transaction;
    }

    /// <summary>
    /// Pins a page for modification by a transaction: acquires the page write lock
    /// and captures the pre-image on first touch.
    /// </summary>
    private StoragePageHandle TouchPage(StorageTransaction transaction, PageId pageId)
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
    /// Allocates and initializes a fresh data page for an owner's chain inside a
    /// transaction. The owner tag is stamped before the pre-image is captured, so
    /// rollback restores an allocated-but-empty page still belonging to the chain —
    /// a safe leak the owner's next insert reuses.
    /// </summary>
    private StoragePageHandle AllocateDataPage(StorageTransaction transaction, ulong ownerId, out SlottedPage slotted)
    {
        var handle = _pageManager!.AllocatePage(PageType.Data);

        try
        {
            slotted = new SlottedPage(handle.Page);
            slotted.Initialize();

            var page = handle.Page;
            page.OwnerId = ownerId;

            RegisterOwnerPage(ownerId, handle.Id);
            RegisterTouch(transaction, handle);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private unsafe void RegisterTouch(StorageTransaction transaction, StoragePageHandle handle)
    {
        long pageId = (long)handle.Id;
        bool locked = false;

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
                locked = true;
            }
        }

        if (transaction.HasTouched(pageId))
        {
            return;
        }

        var page = handle.Page;
        var current = new ReadOnlySpan<byte>(page.Pointer, Page.Size);
        byte[] scratch = ArrayPool<byte>.Shared.Rent(PageImageCodec.MaximumRunsLength);
        try
        {
            // The pre-image, encoded as the page's non-zero byte runs: a freshly allocated page or
            // one with a large free gap costs a few hundred bytes, not 8 KiB.
            int length = PageImageCodec.EncodeImage(current, scratch);
            var runs = scratch.AsSpan(0, length);

            // Rule F: a page at or below the redo point has no record in the journal, so its first
            // change journals its full image, the base every later record of the page chains onto
            // (invariant P). Past the transaction's pre-image budget the image is journaled anyway,
            // so the transaction keeps only where it lies (the spill).
            bool imageNeeded = page.Lsn <= Volatile.Read(ref _redoLsn);
            bool spill = transaction.PreImageBytes + length + StorageTransaction.PreImageOverhead > PreImageBudget;
            if (imageNeeded || spill)
            {
                if (imageNeeded)
                {
                    // The audit for a page with no record since the checkpoint: the image is about
                    // to make whatever the pool holds committed content, so it must be what the
                    // checkpoint wrote (an allocation's clear is exempt).
                    _consistency?.CheckImagingTouch(pageId, current, ReadStoredPage);
                }

                long lsn = _journal!.AppendPageRecord(transaction.Sequence, handle.Id, JournalRecordType.FullPageImage, runs, out var location);
                _consistency?.ImageJournaled(pageId, lsn, runs);

                // Stamp the page so the write-ahead gate makes the image durable before any stolen
                // write of the page reaches the data file, and keep the stamp: a page evicted clean
                // would reload with its older LSN, and the next delta would name the wrong base.
                page.Lsn = lsn;
                handle.MarkDirty();

                if (spill)
                {
                    transaction.RecordSpilledPreImage(pageId, location);
                    Interlocked.Increment(ref _spilledPreImages);
                    return;
                }

                AccountPreImage(transaction.RecordPreImage(pageId, runs.ToArray(), lsn));
                return;
            }

            // The page already has its image in the journal: nothing is journaled now, and its
            // next delta chains onto the LSN it carries. The audit: the pre-image must be exactly
            // what recovery rebuilds at that LSN.
            _consistency?.CheckUnimagedTouch(pageId, current);
            AccountPreImage(transaction.RecordPreImage(pageId, runs.ToArray(), page.Lsn));
        }
        catch
        {
            // The page has no pre-image in this transaction, so neither its commit nor its rollback
            // (both release by pre-image) would release the lock taken above, and every later
            // transaction touching the page would be refused. Its image may be in the journal
            // already (a failure after the append); that only repeats the page's committed content.
            if (locked && !transaction.HasTouched(pageId))
            {
                lock (_transactionLock)
                {
                    _pageWriteLocks.Remove(pageId);
                }
            }

            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    /// <summary>
    /// Adds a pre-image's cost to the storage's total of pre-image bytes and its peak.
    /// </summary>
    private void AccountPreImage(long cost)
    {
        long total = Interlocked.Add(ref _preImageBytes, cost);
        long peak = Interlocked.Read(ref _peakPreImageBytes);
        while (total > peak)
        {
            long seen = Interlocked.CompareExchange(ref _peakPreImageBytes, total, peak);
            if (seen == peak)
            {
                break;
            }

            peak = seen;
        }
    }

    /// <summary>
    /// Zeroes a pooled page's body. The length is the pool buffer's fixed size, never the
    /// overflow size recorded in the page's own header: a header is page content, and a
    /// corrupt one must not decide how far past the buffer a clear runs.
    /// </summary>
    private static unsafe void ClearBody(Page page)
        => new Span<byte>(page.Pointer + Page.HeaderSize, Page.Size - Page.HeaderSize).Clear();

    /// <summary>
    /// Ends a bracket: releases its page write locks and its place in the active count and, for a
    /// committed bracket, then returns the pages it released to the allocator
    /// (<see cref="CompleteCommitted"/>).
    /// </summary>
    /// <param name="transaction">The bracket to end.</param>
    /// <param name="applyPendingFrees">True when the bracket committed, so its page releases take effect.</param>
    private void ReleasePageWriteLocks(StorageTransaction transaction, bool applyPendingFrees = false)
    {
        lock (_transactionLock)
        {
            foreach (long pageId in transaction.PreImages.Keys)
            {
                if (_pageWriteLocks.TryGetValue(pageId, out long owner) && owner == transaction.Sequence)
                {
                    _pageWriteLocks.Remove(pageId);
                }
            }

            // The transaction's pre-images are released with it.
            Interlocked.Add(ref _preImageBytes, -transaction.PreImageBytes);

            // Commit and rollback each end exactly one begun transaction (the scope
            // guards double completion), so the active count pairs with
            // BeginTransaction's increment. The scope completes in the same step, so a
            // completion that throws after this point is never repeated by Dispose.
            _activeTransactionCount--;
            transaction.MarkCompleted();

            // After the locks above and under the same lock, so no page is on the free list while
            // it is write-locked (CompleteCommitted).
            if (applyPendingFrees)
            {
                ApplyPendingFrees(transaction);
            }
        }
    }

    /// <summary>
    /// Writes a new header generation into the slot of page 0 that does not hold the newest
    /// one: the anchor's overflow pages of that slot first, the journal through its last
    /// record, every dirty page and a durable data flush, then the slot itself and another
    /// durable data flush. Only once the slot is durable does it become the newest; a failure
    /// or a crash before that leaves the other slot as the header open reads, and a torn slot
    /// write fails its checksum.
    /// </summary>
    /// <param name="anchor">The checkpoint anchor the generation records.</param>
    /// <remarks>
    /// <para>
    /// <b>The write-ahead rule for unjournaled pages.</b> The anchor's overflow pages are
    /// written outside the journal, and allocation can hand them a page whose free is still
    /// in a journal tail that is not durable: a bracket committed with
    /// <c>awaitDurability: false</c> returns its pages to the allocator as soon as its commit
    /// record is appended. Overwriting such a page durably before that commit record is
    /// durable would lose its content with nothing left to restore it. So the journal is made
    /// durable through its last record once the chain's pages are allocated, and each chain
    /// page carries that LSN, so the buffer pool's write-ahead gate enforces the same order on
    /// any write-back of it (an eviction or the page writer) that comes first. PostgreSQL
    /// flushes WAL up to a buffer's LSN before every data write (<c>FlushBuffer</c>,
    /// <c>src/backend/storage/buffer/bufmgr.c:4567-4585</c>), and flushes the checkpoint
    /// record before it updates the control file (<c>CreateCheckPoint</c>,
    /// <c>src/backend/access/transam/xlog.c:8055</c> and <c>8140</c>).
    /// </para>
    /// <para>
    /// <b>A failed slot write takes the storage offline (#1268).</b> Once the slot write is
    /// issued, a failure (a failed write or fsync) leaves the slot either the previous generation
    /// or, on the media already, the newest one pointing at this chain. A retry would rewrite that
    /// chain in place at the same generation, and a crash during it would leave the newest slot
    /// pointing at pages that do not verify, so no header write may run again in this process,
    /// as PostgreSQL stops on a failed control-file write or fsync
    /// (<c>src/common/controldata_utils.c:245-265</c>). Without header writes no checkpoint can
    /// truncate the journal, so the storage goes offline exactly as a failed durable flush takes it
    /// (<see cref="StorageOfflineException"/>): every later write is refused, its close writes
    /// nothing, and the engines refuse every operation of its database until it is reopened.
    /// Before #1268 the storage only refused later header writes, and its database kept accepting
    /// commits while its journal grew without bound.
    /// </para>
    /// <para>
    /// The LSN floor is the journal's last LSN: every LSN a data page carries came from a
    /// journal record, so the floor bounds them all even after the journal is truncated. The
    /// sequence floor is the highest sequence assigned so far; a checkpoint holds the
    /// transaction lock, under which sequences are assigned, so for a checkpoint it is exact.
    /// </para>
    /// </remarks>
    /// <exception cref="StorageOfflineException">
    /// The storage is offline, a durable flush this write made failed, or the slot write or the
    /// flush after it failed; the last two take the storage offline.
    /// </exception>
    private void WriteHeader(ReadOnlySpan<long> anchor)
    {
        if (_pageManager is null || _journal is null || _disposed)
        {
            return;
        }

        lock (_headerLock)
        {
            // A failed slot write took the storage offline (the catch below), so this refuses
            // every header write after one too.
            ThrowIfOffline();

            int target = 1 - _headerSlot;
            long generation = _headerGeneration + 1;
            long sequenceFloor = Math.Max(_sequenceFloor, Interlocked.Read(ref _nextTransactionSequence));

            int inline = Math.Min(anchor.Length, StorageHeaderPage.InlineAnchorCapacity);
            var overflow = anchor[inline..];
            ResizeAnchorChain(target, (overflow.Length + StorageHeaderPage.AnchorPageCapacity - 1) / StorageHeaderPage.AnchorPageCapacity);

            // Read after the chain's allocations: every free that returned one of its pages to
            // the allocator appended its commit record before it did.
            long writeAheadLsn = _journal.LastLsn;
            long chainHead = WriteAnchorChain(target, generation, overflow, writeAheadLsn);
            long lsnFloor = Math.Max(_lsnFloor, writeAheadLsn);

            // The journal first, then everything the new generation points at, durable before
            // the generation is. A durable flush that fails here takes the storage offline: the
            // pages just written back are recorded clean, so a retry's flush could succeed over
            // write-backs the operating system dropped and truncate the journal that holds them.
            FlushWriteAhead(writeAheadLsn);
            _pageManager.FlushAll();
            FlushData();

            var state = new StorageHeaderSlot(
                Generation: generation,
                LsnFloor: lsnFloor,
                SequenceFloor: sequenceFloor,
                TotalPageCount: _pageManager.PageCount,
                FreePageCount: _pageManager.FreePageCount,
                ModifiedAtUtcTicks: DateTime.UtcNow.Ticks,
                AnchorCount: anchor.Length,
                AnchorInlineCount: inline,
                AnchorChainHead: chainHead,
                AnchorChainPageCount: _anchorChains[target].Count);

            // A write to slot 0 restores page 0's identity block when open had to take the
            // identity from a slot: the block shares slot 0's 4 KiB block, so the whole block
            // is written at once, and slot 1 keeps a full copy until this write is durable.
            // Writing the identity block beside slot 1 would put the newest slot (0) at risk.
            bool repair = _identityNeedsRepair && target == 0;
            byte[] bytes;
            long offset;
            if (repair)
            {
                bytes = new byte[StorageHeaderPage.LeadingBlockSize];
                StorageHeaderPage.ComposePageStart(bytes, _identity);
                StorageHeaderPage.WriteSlot(bytes.AsSpan(StorageHeaderPage.Slot0Offset), state, _identity, anchor[..inline]);
                offset = 0;
            }
            else
            {
                bytes = new byte[StorageHeaderPage.SlotSize];
                StorageHeaderPage.WriteSlot(bytes, state, _identity, anchor[..inline]);
                offset = StorageHeaderPage.SlotOffset(target);
            }

            try
            {
                Data.Write(bytes, offset);
                FlushData();
            }
            catch (Exception exception)
            {
                // Whatever failed, the slot may already be on the media (see the remarks), so the
                // storage goes offline. A failed durable flush already took it offline (FlushData);
                // any other failure takes it offline here, unless something else (a drain of the
                // journal on another thread) already had, which this write then reports instead of
                // its own failure. An OutOfMemoryException propagates as itself once the storage is
                // offline.
                _headerFaulted = true;
                if (exception is StorageOfflineException)
                {
                    throw;
                }

                var offline = GoOffline(StorageOfflineException.HeaderWriteFailed(exception));
                if (exception is OutOfMemoryException)
                {
                    throw;
                }

                throw offline;
            }

            _headerSlot = target;
            _headerGeneration = generation;
            _lsnFloor = lsnFloor;
            _sequenceFloor = sequenceFloor;
            _identityNeedsRepair &= !repair;
            Volatile.Write(ref _checkpointActives, anchor.ToArray());
        }
    }

    /// <summary>
    /// Sizes a slot's chain of anchor pages: frees the pages it no longer needs, then grows
    /// it from the free-space map.
    /// </summary>
    private void ResizeAnchorChain(int slot, int pagesNeeded)
    {
        var chain = _anchorChains[slot];

        while (chain.Count > pagesNeeded)
        {
            FreeAnchorPage(chain[^1]);
            chain.RemoveAt(chain.Count - 1);
        }

        while (chain.Count < pagesNeeded)
        {
            // Recorded on the chain as soon as it is allocated, so a failure before the slot
            // is written still leaves the page to the chain's next write (or to the next
            // open's scan, which frees an anchor page no valid generation chains). The allocation
            // reports the page to the consistency check as written outside the journal.
            using var allocated = _pageManager!.AllocatePage(PageType.CheckpointAnchor);
            chain.Add((long)allocated.Id);
        }
    }

    /// <summary>
    /// Writes the anchor sequences a slot cannot hold onto that slot's chain of anchor pages,
    /// which <see cref="ResizeAnchorChain"/> sized. The pages are written through the buffer
    /// pool, where the caller's flush makes them durable.
    /// </summary>
    /// <param name="slot">The header slot that owns the chain.</param>
    /// <param name="generation">The generation being written.</param>
    /// <param name="overflow">The anchor sequences the slot does not hold.</param>
    /// <param name="lsn">The page LSN each chain page carries (see <see cref="WriteHeader"/>).</param>
    /// <returns>The first page of the chain, or zero when the slot holds the whole anchor.</returns>
    private long WriteAnchorChain(int slot, long generation, ReadOnlySpan<long> overflow, long lsn)
    {
        var chain = _anchorChains[slot];
        int pagesNeeded = chain.Count;

        for (int index = 0; index < pagesNeeded; index++)
        {
            int start = index * StorageHeaderPage.AnchorPageCapacity;
            int count = Math.Min(StorageHeaderPage.AnchorPageCapacity, overflow.Length - start);
            long next = index + 1 < pagesNeeded ? chain[index + 1] : 0L;

            // Written outside the journal, on purpose: recovery never replays onto the live chain.
            _consistency?.Forget(chain[index]);
            using var handle = _pageManager!.PinForOverwrite((PageId)chain[index]);
            unsafe
            {
                StorageHeaderPage.WriteAnchorPage(
                    new Span<byte>(handle.Page.Pointer, Page.Size),
                    chain[index],
                    lsn,
                    generation,
                    slot,
                    index,
                    overflow.Slice(start, count),
                    next);
            }

            handle.MarkDirty();
        }

        return pagesNeeded > 0 ? chain[0] : 0L;
    }

    /// <summary>
    /// Returns an anchor page to the allocator: retyped <see cref="PageType.Free"/> in the
    /// pool (the next flush writes it) and freed in the free-space map. Anchor pages are
    /// unjournaled, so nothing else needs to know.
    /// </summary>
    private unsafe void FreeAnchorPage(long pageId)
    {
        _consistency?.Forget(pageId);
        using (var handle = _pageManager!.PinForOverwrite((PageId)pageId))
        {
            var page = handle.Page;
            new Span<byte>(page.Pointer, Page.Size).Clear();
            page.Id = pageId;
            page.Type = PageType.Free;
            handle.MarkDirty();
        }

        _freeSpaceMap.Free((PageId)pageId);
    }

    /// <summary>
    /// Reads page 0 of an existing file: the format fence first, from the raw bytes and
    /// before any checksum, then the newest header slot that verifies, the identity block
    /// (or, when it does not verify, the newest slot's copy of it), and the slot's checkpoint
    /// anchor chain. Sets the identity and the header state.
    /// </summary>
    /// <param name="anchor">The checkpoint anchor the newest generation records.</param>
    /// <param name="anchorChain">The anchor pages of that generation, in chain order.</param>
    /// <returns>The newest valid header slot.</returns>
    /// <remarks>
    /// The identity block shares slot 0's 4 KiB block. On a drive with 4 KiB physical sectors
    /// that emulates 512-byte ones, power lost during a slot-0 write can leave that whole
    /// physical sector unreadable or garbage — magic and format version included — while slot
    /// 1 is intact. A page 0 whose magic or identity checksum fails is therefore opened from
    /// the newest valid slot's copy of the identity block, and only a page 0 with neither a
    /// verified identity block nor a valid slot is refused: as not a storage file when its
    /// magic is wrong, as corruption otherwise.
    /// </remarks>
    private StorageHeaderSlot ReadHeader(out long[] anchor, out List<long> anchorChain)
    {
        if (Data.Length < Page.Size)
        {
            throw new StorageIOException(
                $"Invalid storage file: it holds {Data.Length} bytes, less than its {Page.Size}-byte header page.");
        }

        var page0 = new byte[Page.Size];
        Data.ReadPage((PageId)0L, page0);

        // The format fence reads the two fields every storage format keeps at the same
        // offsets, before any checksum: the checksum algorithm is itself part of the format,
        // so a file of another format would otherwise be reported as corrupt (PostgreSQL's
        // ReadControlFile checks pg_control_version before its CRC for the same reason).
        var (magic, formatVersion) = StorageHeaderPage.ReadFormat(page0);
        if (magic == StorageFileHeader.ExpectedMagic && formatVersion != StorageFileHeader.CurrentFormatVersion)
        {
            throw StorageFormatException.ForDataFile(formatVersion);
        }

        StorageHeaderSlot? newest = null;
        int newestSlot = -1;
        for (int slot = 0; slot < 2; slot++)
        {
            var bytes = page0.AsSpan(StorageHeaderPage.SlotOffset(slot), StorageHeaderPage.SlotSize);
            if (StorageHeaderPage.TryReadSlot(bytes, out var candidate) && (newest is null || candidate!.Generation > newest.Generation))
            {
                newest = candidate;
                newestSlot = slot;
            }
        }

        var identity = StorageHeaderPage.IdentityBlock(page0);
        if (magic != StorageFileHeader.ExpectedMagic || !StorageHeaderPage.VerifyIdentity(identity))
        {
            if (newest is null)
            {
                throw magic != StorageFileHeader.ExpectedMagic
                    ? new StorageIOException("Invalid storage file: header magic number mismatch, and no header slot verifies.")
                    : new StorageCorruptionException(
                        (PageId)0L,
                        "Invalid storage file: the identity block of page 0 failed checksum verification, and neither header slot " +
                        "verifies to restore it from.");
            }

            identity = StorageHeaderPage.SlotIdentity(page0.AsSpan(StorageHeaderPage.SlotOffset(newestSlot), StorageHeaderPage.SlotSize));
            var (copyMagic, copyVersion) = StorageHeaderPage.ReadIdentityFormat(identity);
            if (copyMagic != StorageFileHeader.ExpectedMagic || !StorageHeaderPage.VerifyIdentity(identity))
            {
                throw new StorageCorruptionException(
                    (PageId)0L,
                    $"Invalid storage file: the identity block of page 0 does not verify, and neither does the copy header slot " +
                    $"{newestSlot} (generation {newest.Generation}) carries.");
            }

            if (copyVersion != StorageFileHeader.CurrentFormatVersion)
            {
                throw StorageFormatException.ForDataFile(copyVersion);
            }

            _identityNeedsRepair = true;
        }
        else if (newest is null)
        {
            throw new StorageCorruptionException(
                (PageId)0L,
                "Invalid storage file: neither header slot of page 0 verifies. Each header write leaves the other slot intact, " +
                "so both failing means page 0 was damaged outside a header write.");
        }

        _identity = identity.ToArray();
        (_id, _name) = StorageHeaderPage.ReadIdentity(_identity);

        anchor = new long[newest.AnchorCount];
        StorageHeaderPage.ReadInlineAnchor(
            page0.AsSpan(StorageHeaderPage.SlotOffset(newestSlot), StorageHeaderPage.SlotSize),
            anchor.AsSpan(0, newest.AnchorInlineCount));
        anchorChain = ReadAnchorChain(newest, newestSlot, anchor.AsSpan(newest.AnchorInlineCount));

        _headerSlot = newestSlot;
        _headerGeneration = newest.Generation;
        _lsnFloor = newest.LsnFloor;
        _sequenceFloor = newest.SequenceFloor;
        return newest;
    }

    /// <summary>
    /// Reads a header generation's anchor chain straight from the data stream. Every page was
    /// durable before the slot that points at it was written, so a page that does not verify
    /// is damage, not a torn write, and the open fails rather than classify the transactions
    /// the anchor names from a partial list.
    /// </summary>
    private List<long> ReadAnchorChain(StorageHeaderSlot header, int slot, Span<long> destination)
    {
        var chain = new List<long>(header.AnchorChainPageCount);
        long pageCount = Data.Length / Page.Size;
        long pageId = header.AnchorChainHead;
        int read = 0;
        var buffer = new byte[Page.Size];

        for (int index = 0; index < header.AnchorChainPageCount; index++)
        {
            if (pageId <= 0 || pageId >= pageCount || chain.Contains(pageId))
            {
                throw new StorageCorruptionException(
                    (PageId)pageId,
                    $"Invalid storage file: header slot {slot} (generation {header.Generation}) chains checkpoint anchor page {pageId} " +
                    $"at position {index}, which is not a page of the file or repeats one.");
            }

            Data.ReadPage((PageId)pageId, buffer);
            string? problem = StorageHeaderPage.TryReadAnchorPage(buffer, pageId, header.Generation, slot, index, out int count, out long next);
            if (problem is null && count > destination.Length - read)
            {
                problem = $"it holds {count} entries, more than the {destination.Length - read} the header slot has left";
            }

            if (problem is not null)
            {
                throw new StorageCorruptionException(
                    (PageId)pageId,
                    $"Invalid storage file: checkpoint anchor page {pageId} of header slot {slot} (generation {header.Generation}) does not verify: {problem}.");
            }

            StorageHeaderPage.ReadAnchorEntries(buffer, destination.Slice(read, count));
            read += count;
            chain.Add(pageId);
            pageId = next;
        }

        if (read != destination.Length || pageId != 0)
        {
            throw new StorageCorruptionException(
                (PageId)0L,
                $"Invalid storage file: header slot {slot} (generation {header.Generation}) records {destination.Length} anchor entries " +
                $"on its chain, but the chain holds {read}.");
        }

        return chain;
    }
}
