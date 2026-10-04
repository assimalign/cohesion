using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Storage;

using Assimalign.Cohesion.Database.Storage.Internal;

/// <summary>
/// Base implementation of the write-ahead log: LSN assignment, append serialization,
/// the CRC-protected frame codec, and torn-tail-tolerant reading. Derived classes
/// provide the physical medium.
/// </summary>
/// <remarks>
/// <para>
/// Frame layout: <c>[int frameLength][int magic][body][uint crc32c(body)]</c> where the
/// body is <c>[byte version][long lsn][long transactionSequence][byte type][long pageId]
/// [payload]</c>. A record whose length prefix, magic, or checksum does not verify
/// terminates the read scan — a torn tail belongs to work that was never acknowledged.
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
/// written after the failure; only reads and the confirmation of an LSN that was already
/// durable still succeed. Reopening the storage runs recovery, which decides what the journal
/// holds. PostgreSQL raises <c>PANIC</c> on a failed WAL fsync for the same reason
/// (<c>issue_xlog_fsync</c>, <c>src/backend/access/transam/xlog.c:9877-9937</c>).
/// </para>
/// </remarks>
public abstract class StorageJournal : IStorageJournal
{
    private readonly object _syncRoot = new();
    private long _lastLsn;
    private long _durableLsn;
    private bool _initialized;
    private bool _disposed;

    // Set once, by the first failed durable flush (or by the owning storage when a durable
    // flush of its data file failed); never cleared. Guarded by _syncRoot for writes.
    private StorageOfflineException? _offline;

    // 1 once Offline was raised for the latch above, so it is raised exactly once.
    private int _offlineRaised;

    // The bytes of verified frames the journal holds since its last truncation, and the bytes
    // the last full read scan verified.
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
    public long LastLsn
    {
        get
        {
            EnsureInitialized();
            return _lastLsn;
        }
    }

    /// <inheritdoc />
    public long DurableLsn
    {
        get
        {
            EnsureInitialized();
            return _durableLsn;
        }
    }

    /// <summary>
    /// Gets the number of bytes of verified frames the journal holds since its last
    /// truncation: what a recovery would read, and what a checkpoint would discard.
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
    /// Gets the error that took this journal offline, or null while it is online. Once set it
    /// stays set: every later append, flush and checkpoint throws a
    /// <see cref="StorageOfflineException"/> carrying the same cause.
    /// </summary>
    public StorageOfflineException? OfflineError => Volatile.Read(ref _offline);

    /// <summary>
    /// Gets whether a failed durable flush took this journal offline.
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
    internal long Checkpoint(ReadOnlySpan<long> activeTransactions, bool forceDurable)
    {
        ThrowIfDisposed();
        EnsureInitialized();

        Span<byte> payload = activeTransactions.Length <= 64
            ? stackalloc byte[activeTransactions.Length * sizeof(long)]
            : new byte[activeTransactions.Length * sizeof(long)];

        for (int i = 0; i < activeTransactions.Length; i++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(payload.Slice(i * sizeof(long), sizeof(long)), activeTransactions[i]);
        }

        try
        {
            lock (_syncRoot)
            {
                ThrowIfOfflineLocked();
                TruncateCore();
                _length = 0;
                _checkpointSignaled = false;
                long lsn = AppendLocked(0, JournalRecordType.Checkpoint, default, payload);
                FlushLocked(forceDurable);
                if (forceDurable)
                {
                    _durableLsn = _lastLsn;
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
                _lastLsn = floor;
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// An LSN that was already durable is confirmed even after the journal went offline: its
    /// durability was established before the failure. Any other request on an offline journal
    /// throws <see cref="StorageOfflineException"/>, and a durable flush that fails takes the
    /// journal offline.
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
                FlushLocked(forceDurable: true);
                _durableLsn = _lastLsn;
            }
        }
        finally
        {
            RaiseOffline();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// An offline journal refuses every flush with <see cref="StorageOfflineException"/>, and a
    /// durable flush that fails takes the journal offline.
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
                FlushLocked(forceDurable);

                if (forceDurable)
                {
                    _durableLsn = _lastLsn;
                }
            }
        }
        finally
        {
            RaiseOffline();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<JournalRecord> ReadAll()
    {
        ThrowIfDisposed();
        EnsureInitialized();

        lock (_syncRoot)
        {
            return ReadAllCore();
        }
    }

    /// <summary>Enumerates journal records without retaining page-image payloads from earlier records.</summary>
    /// <returns>The verifiable records in append order, ending at the first torn frame.</returns>
    /// <remarks>
    /// Enumeration holds the journal's synchronous append lock until disposed. Consume it
    /// synchronously on one thread, and do not append, checkpoint, or await within the loop.
    /// Recovery uses this path so a journal larger than available memory can be replayed.
    /// </remarks>
    public IEnumerable<JournalRecord> ReadSequential()
    {
        ThrowIfDisposed();
        EnsureInitialized();
        lock (_syncRoot)
        {
            foreach (var record in ReadRecordsCore())
            {
                yield return record;
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeCore();
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

        long lsn;
        Action? checkpointNeeded = null;
        lock (_syncRoot)
        {
            ThrowIfOfflineLocked();
            lsn = AppendLocked(transactionSequence, type, pageId, payload);

            if (_checkpointThreshold > 0 && _length >= _checkpointThreshold && !_checkpointSignaled)
            {
                _checkpointSignaled = true;
                checkpointNeeded = _checkpointNeeded;
            }
        }

        // Outside the lock: the hook only wakes a worker, which takes its own locks.
        checkpointNeeded?.Invoke();
        return lsn;
    }

    /// <summary>
    /// Flushes under the append lock; a durable flush that fails takes the journal offline and
    /// throws <see cref="StorageOfflineException"/> carrying the failure.
    /// </summary>
    private void FlushLocked(bool forceDurable)
    {
        if (!forceDurable)
        {
            FlushCore(forceDurable: false);
            return;
        }

        try
        {
            FlushCore(forceDurable: true);
        }
        catch (Exception exception) when (exception is not (StorageOfflineException or ObjectDisposedException or NotSupportedException))
        {
            // NotSupportedException is a configuration error (a durable request on a handle that
            // cannot flush durably), raised before any byte is flushed, not a failed fsync.
            var offline = StorageOfflineException.Create(StorageOfflineException.JournalFlushOperation, exception);
            SetOfflineLocked(offline);
            throw offline;
        }
    }

    private void ThrowIfOfflineLocked()
    {
        if (_offline is { } offline)
        {
            throw StorageOfflineException.Refusal(offline);
        }
    }

    private long AppendLocked(long transactionSequence, JournalRecordType type, PageId pageId, ReadOnlySpan<byte> payload)
    {
        long lsn = _lastLsn + 1;

        int bodyLength = BodyHeaderSize + payload.Length;
        byte[] frame = new byte[FramePrefixSize + bodyLength + sizeof(uint)];
        var span = frame.AsSpan();

        BinaryPrimitives.WriteInt32LittleEndian(span, bodyLength);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], Magic);

        var body = span.Slice(FramePrefixSize, bodyLength);
        body[0] = CurrentVersion;
        BinaryPrimitives.WriteInt64LittleEndian(body[1..], lsn);
        BinaryPrimitives.WriteInt64LittleEndian(body[9..], transactionSequence);
        body[17] = (byte)type;
        BinaryPrimitives.WriteInt64LittleEndian(body[18..], (long)pageId);
        payload.CopyTo(body[BodyHeaderSize..]);

        uint checksum = Crc32C.Compute(body);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(FramePrefixSize + bodyLength)..], checksum);

        AppendFrame(frame);
        _lastLsn = lsn;
        _length += frame.Length;
        return lsn;
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

            _lastLsn = lastLsn;
            _length = _scannedLength;
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
    /// Appends a complete encoded frame to the physical medium. Frames must be
    /// persisted in append order.
    /// </summary>
    /// <param name="frame">Encoded frame bytes.</param>
    protected abstract void AppendFrame(ReadOnlySpan<byte> frame);

    /// <summary>
    /// Flushes pending journal bytes to the physical medium.
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
