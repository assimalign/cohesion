using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Storage;

using Assimalign.Cohesion.Database.Storage.Internal;

/// <summary>
/// Base implementation of the write-ahead log: LSN assignment, append serialization,
/// the CRC-protected frame codec, the user-space append buffer, and torn-tail-tolerant
/// reading. Derived classes provide the physical medium.
/// </summary>
/// <remarks>
/// <para>
/// Frame layout: <c>[int frameLength][int magic][body][uint crc32c(body)]</c> where the
/// body is <c>[byte version][long lsn][long transactionSequence][byte type][long pageId]
/// [payload]</c>. A record whose length prefix, magic, or checksum does not verify
/// terminates the read scan — a torn tail belongs to work that was never acknowledged.
/// </para>
/// <para>
/// <b>The append buffer (#1252).</b> An append encodes its frame in place in a user-space
/// buffer and assigns its LSN; it issues no system call. The buffer is handed to the medium
/// in one write (a <em>drain</em>) when it is full and at every point that needs the records
/// on the medium: before a commit is acknowledged in every durability mode
/// (<see cref="EnsureDurable"/>, and the storage's <see cref="EnsureWritten"/> for
/// <see cref="StorageCommitDurability.None"/>), before every reader (<see cref="ReadAll"/>,
/// <see cref="ReadSequential"/>), before the write-ahead gate lets a page reach the data file,
/// before a checkpoint truncates, by <see cref="Flush"/>, and when the journal is disposed.
/// So <see cref="LastLsn"/> is the last LSN assigned, <see cref="WrittenLsn"/> the last one the
/// medium holds, and <see cref="DurableLsn"/> the last one a durable flush confirmed. This is
/// PostgreSQL's WAL buffer: <c>XLogInsertRecord</c> copies a record into the WAL buffers
/// (<c>CopyXLogRecordToWAL</c>, <c>src/backend/access/transam/xlog.c:1323</c>),
/// <c>AdvanceXLInsertBuffer</c> writes the oldest buffer out when an insert needs its space
/// (<c>xlog.c:2083-2157</c>), and <c>XLogWrite</c>/<c>XLogFlush</c> write the buffers out in as
/// few <c>pg_pwrite</c> calls as their layout allows, then fsync (<c>xlog.c:2382</c>,
/// <c>2480-2532</c>, <c>2861</c>), tracking the written and flushed positions apart
/// (<c>XLogwrtResult</c>, <c>xlog.c:332-336</c>). Unlike PostgreSQL's
/// <c>synchronous_commit = off</c>, which acknowledges a commit while its record is still in
/// the WAL buffers (<c>RecordTransactionCommit</c>, <c>src/backend/access/transam/xact.c:1553-1565</c>),
/// a commit here is never acknowledged before its record left the process, so a process crash
/// loses no acknowledged commit in any durability mode.
/// </para>
/// <para>
/// <b>A failed drain takes the journal offline (#1252), like a failed durable flush.</b> The
/// buffered records belong to work no caller was told is durable, but pages in the buffer pool
/// already carry their LSNs, and a before image that never reaches the medium could not undo a
/// stolen page. The failing call throws <see cref="StorageOfflineException"/> and nothing more
/// is written; the reopen's recovery reads what the medium holds. PostgreSQL raises
/// <c>PANIC</c> on any failed WAL write ("could not write to log file",
/// <c>src/backend/access/transam/xlog.c:2514-2532</c>).
/// </para>
/// <para>
/// A frame whose checksum verifies but whose version byte is not <see cref="CurrentVersion"/>
/// is not a torn tail: it was written whole, in a format this engine does not read, and
/// stopping the scan there would silently drop it and every record after it. The read
/// refuses it with <see cref="StorageFormatException"/> instead (storage format 2, #1251).
/// The checksum polynomial changed with frame version 3 (CRC-32C), so a frame written by
/// an older engine fails its checksum and reads as a torn tail; the data file's own format
/// fence (<see cref="StorageFileHeader.CurrentFormatVersion"/>) refuses such a file set
/// before its journal is read.
/// </para>
/// <para>
/// LSNs never restart: a reopened journal resumes after its last record, and the storage
/// raises it further to the LSN floor its file header persisted at the last checkpoint
/// (<see cref="RaiseLsnFloor"/>), because a checkpoint truncates the records that would
/// otherwise witness the LSNs already stamped on data pages.
/// </para>
/// <para>
/// <b>A failed durable flush takes the journal offline (#1243).</b> When a durable flush
/// fails, the records appended since the last successful one may or may not be on stable
/// storage, and a retry may report success for bytes the operating system already dropped
/// (PostgreSQL's "fsyncgate"). The failing call throws <see cref="StorageOfflineException"/>,
/// and every later append, flush and checkpoint of this instance throws it too, so nothing is
/// written after the failure; only reads (of what the medium holds) and the confirmation of an
/// LSN that was already durable, or already written, still succeed. Records still in the append
/// buffer are never written. Reopening the storage runs recovery, which decides what the
/// journal holds. PostgreSQL raises <c>PANIC</c> on a failed WAL fsync for the same reason
/// (<c>issue_xlog_fsync</c>, <c>src/backend/access/transam/xlog.c:9877-9937</c>).
/// </para>
/// </remarks>
public abstract class StorageJournal : IStorageJournal
{
    /// <summary>
    /// The size the append buffer starts at, allocated by the first append: 64 KiB, PostgreSQL's
    /// smallest <c>wal_buffers</c> (eight 8 KiB pages, <c>XLOGChooseNumBuffers</c>,
    /// <c>src/backend/access/transam/xlog.c:5244-5255</c>), which holds a statement's page images.
    /// </summary>
    internal const int InitialBufferSize = 64 * 1024;

    /// <summary>
    /// The size the append buffer may grow to when the records appended between two drains do
    /// not fit it: 1 MiB, the 1/32 of the default 32 MiB buffer pool that PostgreSQL gives its WAL
    /// buffers by default (<c>NBuffers / 32</c>, <c>xlog.c:5248</c>). A frame larger than this is
    /// written directly, after the buffer, from a pooled array.
    /// </summary>
    internal const int MaximumBufferSize = 1024 * 1024;

    /// <summary>
    /// The largest payload a frame can carry: what keeps the frame length an <see cref="int"/>.
    /// </summary>
    internal const int MaximumPayloadLength = int.MaxValue - FramePrefixSize - BodyHeaderSize - sizeof(uint);

    private readonly object _syncRoot = new();
    private long _lastLsn;
    private long _writtenLsn;
    private long _durableLsn;
    private bool _initialized;
    private bool _disposed;

    // The append buffer: whole frames, in LSN order, from _writtenLsn + 1 to _lastLsn, that the
    // medium does not hold yet. Guarded by _syncRoot; allocated by the first append, grown by
    // doubling up to _maximumBufferSize, and never shrunk.
    private byte[] _buffer = [];
    private int _buffered;
    private int _maximumBufferSize = MaximumBufferSize;

    // Whether frames were written to the medium since its last flush: a full buffer, a reader
    // and a truncation write without flushing, and a medium that buffers in user space (a
    // FileStream) may still hold them. Guarded by _syncRoot.
    private bool _unflushed;

    // Set once, by the first failed drain or durable flush (or by the owning storage when a
    // durable flush of its data file failed); never cleared. Guarded by _syncRoot for writes.
    private StorageOfflineException? _offline;

    // 1 once Offline was raised for the latch above, so it is raised exactly once.
    private int _offlineRaised;

    // The bytes of frames the journal holds since its last truncation, buffered frames
    // included, and the bytes the last full read scan verified.
    private long _length;
    private long _scannedLength;

    // The size trigger: when an append takes the journal to _checkpointThreshold bytes or
    // more, _checkpointNeeded is invoked once (outside the lock), and again only after the
    // next checkpoint. Guarded by _syncRoot.
    private long _checkpointThreshold;
    private Action? _checkpointNeeded;
    private bool _checkpointSignaled;

    /// <summary>
    /// Initializes a new journal instance.
    /// </summary>
    protected StorageJournal() { }

    /// <inheritdoc />
    /// <remarks>
    /// The record may still be in the append buffer: <see cref="WrittenLsn"/> is the last one
    /// the medium holds. Everything that reads the medium drains the buffer first.
    /// </remarks>
    public long LastLsn
    {
        get
        {
            EnsureInitialized();
            return Volatile.Read(ref _lastLsn);
        }
    }

    /// <summary>
    /// Gets the LSN up to which every record has left the append buffer: the medium holds it
    /// (the operating system has it, durably or not), or a checkpoint truncated it. Records above
    /// it, up to <see cref="LastLsn"/>, are lost if the process stops before the next drain.
    /// </summary>
    public long WrittenLsn
    {
        get
        {
            EnsureInitialized();
            return Volatile.Read(ref _writtenLsn);
        }
    }

    /// <inheritdoc />
    public long DurableLsn
    {
        get
        {
            EnsureInitialized();
            return Volatile.Read(ref _durableLsn);
        }
    }

    /// <summary>
    /// Gets the number of bytes of frames the journal holds since its last truncation,
    /// buffered frames included: what a recovery would read once the buffer drains, and what a
    /// checkpoint would discard.
    /// </summary>
    public long Length
    {
        get
        {
            EnsureInitialized();
            return Volatile.Read(ref _length);
        }
    }

    /// <summary>
    /// Gets the number of bytes in the append buffer that the medium does not hold yet
    /// (diagnostics and tests).
    /// </summary>
    internal int BufferedLength
    {
        get
        {
            lock (_syncRoot)
            {
                return _buffered;
            }
        }
    }

    /// <summary>
    /// Gets the append buffer's current capacity in bytes (diagnostics and tests).
    /// </summary>
    internal int BufferCapacity
    {
        get
        {
            lock (_syncRoot)
            {
                return _buffer.Length;
            }
        }
    }

    /// <summary>
    /// Gets or sets the size the append buffer may grow to (<see cref="MaximumBufferSize"/>
    /// unless a test lowers it to exercise a full buffer or a frame written directly). At least
    /// one frame without payload; set it before the first append.
    /// </summary>
    internal int MaximumBufferBytes
    {
        get => _maximumBufferSize;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, FramePrefixSize + BodyHeaderSize + sizeof(uint));
            lock (_syncRoot)
            {
                _maximumBufferSize = value;
            }
        }
    }

    /// <summary>
    /// Gets the error that took this journal offline, or null while it is online. Once set it
    /// stays set: every later append, flush and checkpoint throws a
    /// <see cref="StorageOfflineException"/> carrying the same cause.
    /// </summary>
    public StorageOfflineException? OfflineError => Volatile.Read(ref _offline);

    /// <summary>
    /// Gets whether a failed drain or durable flush took this journal offline.
    /// </summary>
    public bool IsOffline => OfflineError is not null;

    /// <summary>
    /// Takes the journal offline on behalf of its owner, when a durable flush of the owner's
    /// data file failed: from now on the journal refuses every append, flush and checkpoint.
    /// The first error to take the journal offline is kept.
    /// </summary>
    /// <param name="error">The error that took the owner offline.</param>
    internal void TakeOffline(StorageOfflineException error)
    {
        try
        {
            lock (_syncRoot)
            {
                SetOfflineLocked(error);
            }
        }
        finally
        {
            RaiseOffline();
        }
    }

    /// <summary>
    /// Invoked once, under the journal's lock, when the journal goes offline, so the owning
    /// storage can release anything waiting for a flush that will never come. The handler must
    /// not call back into the journal.
    /// </summary>
    internal Action? WentOffline;

    /// <summary>
    /// Invoked exactly once, with the error that took the journal offline, after the call that
    /// took it offline released the journal's lock and before that call returns or throws: so a
    /// caller that sees the failure sees it after the owner was told. The owning storage uses it
    /// to raise <see cref="Storage.OnOffline"/>; the handler may take other journals' locks.
    /// </summary>
    internal Action<StorageOfflineException>? Offline;

    /// <summary>
    /// Raises <see cref="Offline"/> once the journal is offline and it was not raised yet. Called
    /// outside the journal's lock, in the <c>finally</c> of every call that can take it offline.
    /// </summary>
    private void RaiseOffline()
    {
        if (Volatile.Read(ref _offline) is { } offline && Interlocked.Exchange(ref _offlineRaised, 1) == 0)
        {
            Offline?.Invoke(offline);
        }
    }

    private void SetOfflineLocked(StorageOfflineException error)
    {
        if (_offline is null)
        {
            Volatile.Write(ref _offline, error);
            WentOffline?.Invoke();
        }
    }

    /// <summary>
    /// Arms the size trigger: once an append takes <see cref="Length"/> to
    /// <paramref name="threshold"/> bytes or more, <paramref name="checkpointNeeded"/> is invoked
    /// once, outside the journal's lock, and again only after the next checkpoint truncated the
    /// journal. A threshold of zero or less disarms it.
    /// </summary>
    /// <param name="threshold">The journal length that asks for a checkpoint, in bytes.</param>
    /// <param name="checkpointNeeded">The hook to invoke, or null.</param>
    internal void ConfigureCheckpointTrigger(long threshold, Action? checkpointNeeded)
    {
        lock (_syncRoot)
        {
            _checkpointThreshold = threshold;
            _checkpointNeeded = checkpointNeeded;
            _checkpointSignaled = false;
        }
    }

    /// <inheritdoc />
    public long AppendBegin(long transactionSequence)
        => Append(transactionSequence, JournalRecordType.BeginTransaction, default, ReadOnlySpan<byte>.Empty);

    /// <inheritdoc />
    public long AppendPageImage(long transactionSequence, PageId pageId, JournalRecordType type, ReadOnlySpan<byte> image)
    {
        if (type is not (JournalRecordType.BeforePageImage or JournalRecordType.AfterPageImage))
        {
            throw new ArgumentOutOfRangeException(nameof(type), type, "Page image records must be before or after images.");
        }

        return Append(transactionSequence, type, pageId, image);
    }

    /// <inheritdoc />
    public long AppendOperation(long transactionSequence, ReadOnlySpan<byte> payload)
        => Append(transactionSequence, JournalRecordType.Operation, default, payload);

    /// <inheritdoc />
    public long AppendCommit(long transactionSequence)
        => Append(transactionSequence, JournalRecordType.CommitTransaction, default, ReadOnlySpan<byte>.Empty);

    /// <inheritdoc />
    public long AppendRollback(long transactionSequence)
        => Append(transactionSequence, JournalRecordType.RollbackTransaction, default, ReadOnlySpan<byte>.Empty);

    /// <inheritdoc />
    public long Checkpoint(ReadOnlySpan<long> activeTransactions)
        => Checkpoint(activeTransactions, forceDurable: true);

    /// <summary>
    /// Checkpoints with the storage owner's durability policy. Ordinary flushing
    /// retains truncation and sequence semantics without advancing DurableLsn.
    /// </summary>
    /// <remarks>
    /// The append buffer drains before the truncation, so no record is discarded without having
    /// been written, and a drain that fails stops the checkpoint before it truncates anything.
    /// </remarks>
    internal long Checkpoint(ReadOnlySpan<long> activeTransactions, bool forceDurable)
    {
        ThrowIfDisposed();
        EnsureInitialized();

        if (activeTransactions.Length > MaximumPayloadLength / sizeof(long))
        {
            throw new JournalException($"A checkpoint record cannot list {activeTransactions.Length} transactions: its frame would exceed {int.MaxValue} bytes.");
        }

        try
        {
            lock (_syncRoot)
            {
                ThrowIfOfflineLocked();
                if (_buffered > 0)
                {
                    WriteOutLocked();
                }

                TruncateCore();
                Volatile.Write(ref _length, 0);
                _checkpointSignaled = false;
                long lsn = AppendLocked(0, JournalRecordType.Checkpoint, default, ReadOnlySpan<byte>.Empty, activeTransactions);
                DrainLocked(durable: forceDurable);
                if (forceDurable)
                {
                    Volatile.Write(ref _durableLsn, _lastLsn);
                }
                return lsn;
            }
        }
        finally
        {
            RaiseOffline();
        }
    }

    /// <summary>
    /// Raises the last assigned LSN to at least <paramref name="floor"/>, so the next
    /// record continues above it. The storage calls this once at open, before anything
    /// appends, with the LSN floor its file header persisted at the last checkpoint: a
    /// checkpoint truncates the journal, and when its own record is then lost the journal
    /// alone would restart LSNs below those already stamped on data pages.
    /// </summary>
    /// <param name="floor">The lowest LSN the next record may follow.</param>
    internal void RaiseLsnFloor(long floor)
    {
        ThrowIfDisposed();
        EnsureInitialized();

        lock (_syncRoot)
        {
            if (floor > _lastLsn)
            {
                // Called before anything is appended, so the buffer is empty and the medium holds
                // every record below the floor that it will ever hold.
                Volatile.Write(ref _lastLsn, floor);
                if (_buffered == 0)
                {
                    Volatile.Write(ref _writtenLsn, floor);
                }
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The append buffer drains first, then the medium is flushed durably. An LSN that was
    /// already durable is confirmed even after the journal went offline: its durability was
    /// established before the failure. Any other request on an offline journal throws
    /// <see cref="StorageOfflineException"/>, and a drain or a durable flush that fails takes
    /// the journal offline.
    /// </remarks>
    public void EnsureDurable(long lsn)
    {
        ThrowIfDisposed();
        EnsureInitialized();

        try
        {
            lock (_syncRoot)
            {
                if (_durableLsn >= lsn)
                {
                    return;
                }

                ThrowIfOfflineLocked();
                DrainLocked(durable: true);
                Volatile.Write(ref _durableLsn, _lastLsn);
            }
        }
        finally
        {
            RaiseOffline();
        }
    }

    /// <summary>
    /// Guarantees that every record up to <paramref name="lsn"/> has left the append buffer and
    /// reached the operating system, without a durable flush: what a commit needs before it is
    /// acknowledged under <see cref="StorageCommitDurability.None"/>, and what the write-ahead
    /// gate needs before a page reaches the data file when nothing is flushed durably. A process
    /// crash then loses none of those records; a power loss may.
    /// </summary>
    /// <param name="lsn">The LSN that must have left the buffer.</param>
    /// <remarks>
    /// An LSN that was already written is confirmed even after the journal went offline. Any
    /// other request on an offline journal throws <see cref="StorageOfflineException"/>, and a
    /// drain that fails takes the journal offline.
    /// </remarks>
    internal void EnsureWritten(long lsn)
    {
        ThrowIfDisposed();
        EnsureInitialized();

        try
        {
            lock (_syncRoot)
            {
                if (_writtenLsn >= lsn && !_unflushed)
                {
                    return;
                }

                ThrowIfOfflineLocked();
                DrainLocked(durable: false);
            }
        }
        finally
        {
            RaiseOffline();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The append buffer drains first. An offline journal refuses every flush with
    /// <see cref="StorageOfflineException"/>, and a drain or a durable flush that fails takes the
    /// journal offline.
    /// </remarks>
    public void Flush(bool forceDurable = false)
    {
        ThrowIfDisposed();
        EnsureInitialized();

        try
        {
            lock (_syncRoot)
            {
                ThrowIfOfflineLocked();
                DrainLocked(forceDurable);

                if (forceDurable)
                {
                    Volatile.Write(ref _durableLsn, _lastLsn);
                }
            }
        }
        finally
        {
            RaiseOffline();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The append buffer drains first, so the list ends at <see cref="LastLsn"/>. On an offline
    /// journal nothing is written: the list is what the medium holds, which is what the reopen's
    /// recovery reads. A drain that fails takes the journal offline and throws
    /// <see cref="StorageOfflineException"/>.
    /// </remarks>
    public IReadOnlyList<JournalRecord> ReadAll()
    {
        ThrowIfDisposed();
        EnsureInitialized();

        try
        {
            lock (_syncRoot)
            {
                DrainForReadLocked();
                return ReadAllCore();
            }
        }
        finally
        {
            RaiseOffline();
        }
    }

    /// <summary>Enumerates journal records without retaining page-image payloads from earlier records.</summary>
    /// <returns>The verifiable records in append order, ending at the first torn frame.</returns>
    /// <remarks>
    /// Enumeration holds the journal's synchronous append lock until disposed. Consume it
    /// synchronously on one thread, and do not append, checkpoint, or await within the loop.
    /// Recovery uses this path so a journal larger than available memory can be replayed. The
    /// append buffer drains when the enumeration starts, under the same lock, as for
    /// <see cref="ReadAll"/>.
    /// </remarks>
    public IEnumerable<JournalRecord> ReadSequential()
    {
        ThrowIfDisposed();
        EnsureInitialized();

        try
        {
            lock (_syncRoot)
            {
                DrainForReadLocked();
                foreach (var record in ReadRecordsCore())
                {
                    yield return record;
                }
            }
        }
        finally
        {
            RaiseOffline();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The append buffer drains before the medium is released, as a clean close must not lose
    /// appended records. An offline journal writes nothing. A drain that fails takes the journal
    /// offline; the medium is released, and the <see cref="StorageOfflineException"/> is thrown.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        StorageOfflineException? failure = null;
        try
        {
            lock (_syncRoot)
            {
                _disposed = true;
                if (_initialized && _offline is null && _buffered > 0)
                {
                    try
                    {
                        DrainLocked(durable: false);
                    }
                    catch (StorageOfflineException offline)
                    {
                        failure = offline;
                    }
                }

                _buffer = [];
                _buffered = 0;
            }
        }
        finally
        {
            RaiseOffline();
            DisposeCore();
        }

        if (failure is not null)
        {
            throw failure;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return default;
    }

    private long Append(long transactionSequence, JournalRecordType type, PageId pageId, ReadOnlySpan<byte> payload)
    {
        ThrowIfDisposed();
        EnsureInitialized();

        if (payload.Length > MaximumPayloadLength)
        {
            throw new JournalException($"A journal record cannot carry {payload.Length} bytes: its frame would exceed {int.MaxValue} bytes.");
        }

        long lsn;
        Action? checkpointNeeded = null;
        try
        {
            lock (_syncRoot)
            {
                ThrowIfOfflineLocked();
                lsn = AppendLocked(transactionSequence, type, pageId, payload, ReadOnlySpan<long>.Empty);

                if (_checkpointThreshold > 0 && _length >= _checkpointThreshold && !_checkpointSignaled)
                {
                    _checkpointSignaled = true;
                    checkpointNeeded = _checkpointNeeded;
                }
            }
        }
        finally
        {
            // An append drains a full buffer, and a drain that fails takes the journal offline.
            RaiseOffline();
        }

        // Outside the lock: the hook only wakes a worker, which takes its own locks.
        checkpointNeeded?.Invoke();
        return lsn;
    }

    /// <summary>
    /// Drains the append buffer for a reader, unless the journal is offline: an offline journal
    /// writes nothing, and its reader sees what the medium holds.
    /// </summary>
    private void DrainForReadLocked()
    {
        if (_offline is null && _buffered > 0)
        {
            WriteOutLocked();
        }
    }

    /// <summary>
    /// Drains the append buffer under the append lock: writes every buffered frame to the medium
    /// in one call, then flushes the medium, durably when <paramref name="durable"/> is set. The
    /// non-durable flush hands bytes a medium keeps in user space of its own (a
    /// <see cref="System.IO.FileStream"/>'s buffer) to the operating system; a physical handle
    /// keeps none, so it costs nothing there. A write or flush that fails takes the journal
    /// offline and throws <see cref="StorageOfflineException"/> carrying the failure.
    /// </summary>
    /// <param name="durable">True to flush the medium durably.</param>
    private void DrainLocked(bool durable)
    {
        WriteOutLocked();
        FlushMediumLocked(durable);
        _unflushed = false;
    }

    /// <summary>
    /// Writes every buffered frame to the medium in one call, without flushing it: what a full
    /// buffer, a reader and a truncation need. A write that fails takes the journal offline.
    /// </summary>
    private void WriteOutLocked()
    {
        if (_buffered > 0)
        {
            WriteFramesLocked(_buffer.AsSpan(0, _buffered));
            _buffered = 0;
            _unflushed = true;
            Volatile.Write(ref _writtenLsn, _lastLsn);
        }
    }

    /// <summary>
    /// Writes whole frames to the medium; a write that fails takes the journal offline.
    /// </summary>
    private void WriteFramesLocked(ReadOnlySpan<byte> frames)
    {
        try
        {
            WriteFramesCore(frames);
        }
        catch (Exception exception) when (exception is not (StorageOfflineException or ObjectDisposedException))
        {
            throw TakeOfflineLocked("a write of the journal", exception);
        }
    }

    /// <summary>
    /// Flushes the medium; a flush that fails takes the journal offline.
    /// </summary>
    private void FlushMediumLocked(bool durable)
    {
        try
        {
            FlushCore(durable);
        }
        catch (Exception exception) when (exception is not (StorageOfflineException or ObjectDisposedException or NotSupportedException))
        {
            // NotSupportedException is a configuration error (a durable request on a handle that
            // cannot flush durably), raised before any byte is flushed, not a failed fsync.
            throw TakeOfflineLocked(durable ? "a durable flush of the journal" : "a write of the journal", exception);
        }
    }

    private StorageOfflineException TakeOfflineLocked(string what, Exception cause)
    {
        var offline = StorageOfflineException.Create(what, cause);
        SetOfflineLocked(offline);
        return offline;
    }

    private void ThrowIfOfflineLocked()
    {
        if (_offline is { } offline)
        {
            throw StorageOfflineException.Refusal(offline);
        }
    }

    /// <summary>
    /// Assigns the next LSN and encodes its frame in place in the append buffer, draining the
    /// buffer first when the frame does not fit and the buffer cannot grow. A frame larger than
    /// the buffer may grow to is encoded in a pooled array and written directly, after the
    /// buffer. The payload is <paramref name="payload"/> followed by <paramref name="words"/>,
    /// little-endian.
    /// </summary>
    private long AppendLocked(long transactionSequence, JournalRecordType type, PageId pageId, ReadOnlySpan<byte> payload, ReadOnlySpan<long> words)
    {
        long lsn = _lastLsn + 1;
        int payloadLength = payload.Length + words.Length * sizeof(long);
        int frameLength = FramePrefixSize + BodyHeaderSize + payloadLength + sizeof(uint);

        if (frameLength > _maximumBufferSize)
        {
            WriteOutLocked();

            byte[] rented = ArrayPool<byte>.Shared.Rent(frameLength);
            try
            {
                var frame = rented.AsSpan(0, frameLength);
                EncodeFrame(frame, lsn, transactionSequence, type, pageId, payload, words);
                WriteFramesLocked(frame);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }

            _unflushed = true;
            Volatile.Write(ref _lastLsn, lsn);
            Volatile.Write(ref _writtenLsn, lsn);
        }
        else
        {
            EncodeFrame(ReserveLocked(frameLength), lsn, transactionSequence, type, pageId, payload, words);
            _buffered += frameLength;
            Volatile.Write(ref _lastLsn, lsn);
        }

        Volatile.Write(ref _length, _length + frameLength);
        return lsn;
    }

    /// <summary>
    /// Returns room for a frame of <paramref name="frameLength"/> bytes at the end of the append
    /// buffer: growing the buffer (by doubling, up to its maximum) when the frame does not fit,
    /// and draining it when it cannot grow far enough, as PostgreSQL writes out the oldest WAL
    /// buffer page an insert needs (<c>AdvanceXLInsertBuffer</c>,
    /// <c>src/backend/access/transam/xlog.c:2083-2157</c>).
    /// </summary>
    private Span<byte> ReserveLocked(int frameLength)
    {
        if (_buffered + frameLength > _maximumBufferSize)
        {
            WriteOutLocked();
        }

        if (_buffer.Length - _buffered < frameLength)
        {
            int capacity = Math.Max(_buffer.Length, Math.Min(InitialBufferSize, _maximumBufferSize));
            while (capacity < _buffered + frameLength)
            {
                capacity = (int)Math.Min((long)capacity * 2, _maximumBufferSize);
            }

            var grown = new byte[capacity];
            _buffer.AsSpan(0, _buffered).CopyTo(grown);
            _buffer = grown;
        }

        return _buffer.AsSpan(_buffered, frameLength);
    }

    /// <summary>
    /// Encodes one frame into <paramref name="frame"/>, which is exactly the frame's length.
    /// </summary>
    private static void EncodeFrame(
        Span<byte> frame,
        long lsn,
        long transactionSequence,
        JournalRecordType type,
        PageId pageId,
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<long> words)
    {
        int bodyLength = frame.Length - FramePrefixSize - sizeof(uint);
        BinaryPrimitives.WriteInt32LittleEndian(frame, bodyLength);
        BinaryPrimitives.WriteInt32LittleEndian(frame[4..], Magic);

        var body = frame.Slice(FramePrefixSize, bodyLength);
        body[0] = CurrentVersion;
        BinaryPrimitives.WriteInt64LittleEndian(body[1..], lsn);
        BinaryPrimitives.WriteInt64LittleEndian(body[9..], transactionSequence);
        body[17] = (byte)type;
        BinaryPrimitives.WriteInt64LittleEndian(body[18..], (long)pageId);
        payload.CopyTo(body[BodyHeaderSize..]);

        var tail = body[(BodyHeaderSize + payload.Length)..];
        for (int i = 0; i < words.Length; i++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(tail[(i * sizeof(long))..], words[i]);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(frame[(FramePrefixSize + bodyLength)..], Crc32C.Compute(body));
    }

    private IReadOnlyList<JournalRecord> ReadAllCore()
    {
        var records = new List<JournalRecord>();
        foreach (var record in ReadRecordsCore())
        {
            records.Add(record);
        }
        return records;
    }

    private IEnumerable<JournalRecord> ReadRecordsCore()
    {
        long frameNumber = 0;
        long verifiedLength = 0;

        foreach (var frame in ReadFrames())
        {
            var body = frame.Span;
            frameNumber++;

            if (body.Length < BodyHeaderSize)
            {
                break;
            }

            // The frame verified, so it was written whole: another version is a format
            // this engine does not read, never a torn tail to stop quietly at.
            if (body[0] != CurrentVersion)
            {
                throw StorageFormatException.ForJournalFrame(frameNumber, body[0], CurrentVersion);
            }

            long lsn = BinaryPrimitives.ReadInt64LittleEndian(body[1..]);
            long transactionSequence = BinaryPrimitives.ReadInt64LittleEndian(body[9..]);
            var type = (JournalRecordType)body[17];
            long pageId = BinaryPrimitives.ReadInt64LittleEndian(body[18..]);
            var payload = frame[BodyHeaderSize..];
            verifiedLength += FramePrefixSize + body.Length + sizeof(uint);

            yield return new JournalRecord(lsn, transactionSequence, type, (PageId)pageId, payload);
        }

        // Reached only when the scan ran to the end of the verified frames.
        _scannedLength = verifiedLength;
    }

    private void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        lock (_syncRoot)
        {
            if (_initialized)
            {
                return;
            }

            // Marked only once the scan completes: a scan refused for its format leaves the
            // journal uninitialized, so every later access refuses it the same way.
            long lastLsn = 0;
            foreach (var record in ReadRecordsCore())
            {
                lastLsn = record.Lsn;
            }

            Volatile.Write(ref _lastLsn, lastLsn);
            Volatile.Write(ref _writtenLsn, lastLsn);
            Volatile.Write(ref _length, _scannedLength);
            _initialized = true;
            // Reading existing bytes does not prove a durable flush occurred:
            // a reopened memory store or live OS cache may contain the same bytes.
            // Only a completed explicit durable flush advances DurableLsn.
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    /// <summary>
    /// Writes one or more complete encoded frames, contiguous and in LSN order, to the physical
    /// medium after the frames it already holds, in as few system calls as the medium allows.
    /// Called under the append lock when the append buffer drains.
    /// </summary>
    /// <param name="frames">Encoded frames; the span is only valid for the duration of the call.</param>
    /// <remarks>
    /// A write that throws takes the journal offline: nothing is written to the medium again, so
    /// an implementation need not remove a partial write; the reopen's read scan stops at it.
    /// </remarks>
    protected abstract void WriteFramesCore(ReadOnlySpan<byte> frames);

    /// <summary>
    /// Flushes the journal bytes the medium was given: to the operating system when
    /// <paramref name="forceDurable"/> is false (a medium that buffers nothing in user space does
    /// nothing), and to durable storage when it is true.
    /// </summary>
    /// <param name="forceDurable">True when durable (power-safe) flush semantics are required.</param>
    protected abstract void FlushCore(bool forceDurable);

    /// <summary>
    /// Reads all verifiable frame bodies from the physical medium in append order,
    /// stopping at the first frame whose length, magic, or checksum does not verify.
    /// </summary>
    /// <returns>The frame bodies (excluding prefix and checksum).</returns>
    protected abstract IEnumerable<ReadOnlyMemory<byte>> ReadFrames();

    /// <summary>
    /// Discards all persisted journal content. Called under the append lock as the
    /// first half of a checkpoint; the checkpoint record is appended immediately after.
    /// </summary>
    protected abstract void TruncateCore();

    /// <summary>
    /// Releases implementation-specific resources.
    /// </summary>
    protected abstract void DisposeCore();

    /// <summary>
    /// Size of the frame prefix: length + magic.
    /// </summary>
    protected const int FramePrefixSize = sizeof(int) + sizeof(int);

    /// <summary>
    /// Size of the fixed body header: version + lsn + transaction sequence + type + page id.
    /// </summary>
    protected const int BodyHeaderSize = 1 + sizeof(long) + sizeof(long) + 1 + sizeof(long);

    /// <summary>
    /// Journal frame format version: 3 since storage format 2 (#1251), whose frames are
    /// checksummed with CRC-32C. A verified frame of any other version is refused with
    /// <see cref="StorageFormatException"/>.
    /// </summary>
    protected const byte CurrentVersion = 3;

    /// <summary>
    /// Journal frame magic value ('WAL2').
    /// </summary>
    protected const int Magic = 0x324C4157;
}
