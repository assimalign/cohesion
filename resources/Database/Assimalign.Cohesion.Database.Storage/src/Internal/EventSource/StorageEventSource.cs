using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Threading;

namespace Assimalign.Cohesion.Database.Storage.Internal;

/// <summary>
/// The storage kernel's diagnostics: the lifecycle of a file set (created, recovered, closed),
/// checkpoints, paced write-back, group commit, the failures that take a storage offline or
/// leave a commit unconfirmed, buffer-pool pressure and checksum failures, and the counters of
/// the device work behind them.
/// </summary>
/// <remarks>
/// <para>
/// Internal by the repository's EventSource convention (<c>.claude/rules/event-source.md</c>).
/// Tools enable it by its assembly name, <c>Assimalign.Cohesion.Database.Storage</c>; applications
/// forward it into their logging with <c>Assimalign.Cohesion.Logging.EventSource</c>. Every model's
/// storage derives from <see cref="Storage"/>, so this one source covers the file sets of all five
/// engines. The <c>database</c> payload is the storage's <see cref="Storage.Name"/>; the buffer pool
/// and the group-commit gate learn it from their storage when it is created or opened, and report
/// an empty name before that.
/// </para>
/// <para>
/// Nothing here costs anything while nobody listens (rule 9): every write is behind
/// <see cref="EventSource.IsEnabled(EventLevel, EventKeywords)"/>, every <c>ToString</c> runs
/// inside it, and a duration's first timestamp is taken only when the event that reports it is
/// enabled (the methods returning a timestamp return zero otherwise, and the matching write is
/// skipped). The counters are maintained whatever the listeners, but only on paths that already
/// write to or wait on a device (rule 10; plan D6): a checkpoint, a durable journal flush, a page
/// read on a pool miss, a page write-back, a grouped commit that flushed inline, a pre-image
/// spilled to the journal. Nothing is counted per pin, per record or per statement.
/// </para>
/// </remarks>
[EventSource(Name = "Assimalign.Cohesion.Database.Storage")]
internal sealed class StorageEventSource : EventSource
{
    public static readonly StorageEventSource Log = new();

    /// <summary>
    /// The keywords that let a tool take one family of the high-volume events without the rest.
    /// Events without a keyword are written whatever keywords a session enables.
    /// </summary>
    public static class Keywords
    {
        /// <summary>Checkpoint start and stop.</summary>
        public const EventKeywords Checkpoints = (EventKeywords)0x1;

        /// <summary>Paced write-back passes.</summary>
        public const EventKeywords WriteBack = (EventKeywords)0x2;

        /// <summary>Group-commit flushes and missed windows.</summary>
        public const EventKeywords GroupCommit = (EventKeywords)0x4;

        /// <summary>Foreground evictions of dirty pages.</summary>
        public const EventKeywords BufferPool = (EventKeywords)0x8;
    }

    private PollingCounter? _currentStoragesCounter;
    private IncrementingPollingCounter? _checkpointRateCounter;
    private IncrementingPollingCounter? _journalFlushRateCounter;
    private IncrementingPollingCounter? _pageReadRateCounter;
    private IncrementingPollingCounter? _pageWriteRateCounter;
    private IncrementingPollingCounter? _foregroundPageWriteRateCounter;
    private IncrementingPollingCounter? _groupCommitSelfFlushRateCounter;
    private IncrementingPollingCounter? _preImageSpillRateCounter;

    private long _currentStorages;
    private long _checkpoints;
    private long _journalFlushes;
    private long _pageReads;
    private long _pageWrites;
    private long _foregroundPageWrites;
    private long _groupCommitSelfFlushes;
    private long _preImagesSpilled;

    private StorageEventSource()
    {
    }

    /// <summary>The storages created or opened and not yet closed.</summary>
    internal long CurrentStorages => Volatile.Read(ref _currentStorages);

    /// <summary>The checkpoints completed since the process started.</summary>
    internal long Checkpoints => Volatile.Read(ref _checkpoints);

    /// <summary>The durable journal flushes completed since the process started.</summary>
    internal long JournalFlushes => Volatile.Read(ref _journalFlushes);

    /// <summary>The pages a buffer pool read from its data file on a miss since the process started.</summary>
    internal long PageReads => Volatile.Read(ref _pageReads);

    /// <summary>The pages a buffer pool wrote back to its data file since the process started.</summary>
    internal long PageWrites => Volatile.Read(ref _pageWrites);

    /// <summary>The dirty pages an eviction wrote back on the evicting thread since the process started.</summary>
    internal long ForegroundPageWrites => Volatile.Read(ref _foregroundPageWrites);

    /// <summary>The grouped commits that flushed the journal inline after their window passed, since the process started.</summary>
    internal long GroupCommitSelfFlushes => Volatile.Read(ref _groupCommitSelfFlushes);

    /// <summary>The pre-images storage transactions spilled to the journal since the process started.</summary>
    internal long PreImagesSpilled => Volatile.Read(ref _preImagesSpilled);

    /// <summary>Counts a storage that was created or opened (the <c>current-storages</c> gauge).</summary>
    [NonEvent]
    internal void CountStorageOpened() => Interlocked.Increment(ref _currentStorages);

    /// <summary>
    /// Uncounts a storage closed for the first time (the <c>current-storages</c> gauge). The storage
    /// calls it once, behind its own flag, and only for a storage it counted open.
    /// </summary>
    [NonEvent]
    internal void CountStorageClosed() => Interlocked.Decrement(ref _currentStorages);

    /// <summary>Counts a completed checkpoint.</summary>
    [NonEvent]
    internal void CountCheckpoint() => Interlocked.Increment(ref _checkpoints);

    /// <summary>Counts a completed durable flush of a journal's medium.</summary>
    [NonEvent]
    internal void CountJournalFlush() => Interlocked.Increment(ref _journalFlushes);

    /// <summary>Counts a page a buffer pool read from its data file on a miss.</summary>
    [NonEvent]
    internal void CountPageRead() => Interlocked.Increment(ref _pageReads);

    /// <summary>Counts a page a buffer pool wrote back to its data file.</summary>
    [NonEvent]
    internal void CountPageWrite() => Interlocked.Increment(ref _pageWrites);

    /// <summary>Counts a dirty page an eviction wrote back on the evicting thread.</summary>
    [NonEvent]
    internal void CountForegroundPageWrite() => Interlocked.Increment(ref _foregroundPageWrites);

    /// <summary>Counts a grouped commit that flushed the journal inline after its window passed.</summary>
    [NonEvent]
    internal void CountGroupCommitSelfFlush() => Interlocked.Increment(ref _groupCommitSelfFlushes);

    /// <summary>Counts a pre-image a storage transaction spilled to the journal.</summary>
    [NonEvent]
    internal void CountPreImageSpilled() => Interlocked.Increment(ref _preImagesSpilled);

    /// <summary>Writes that a new storage file set was created.</summary>
    /// <param name="storage">The storage.</param>
    [NonEvent]
    public void StorageCreated(Storage storage)
    {
        if (IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            StorageCreated(storage.Name.ToString(), storage.Id.ToString(), storage.Model.ToString());
        }
    }

    /// <summary>
    /// Writes that an existing file set's recovery started, and returns the timestamp
    /// <see cref="RecoveryStop"/> measures from, or zero when the event is not enabled.
    /// </summary>
    /// <param name="storage">The storage being opened.</param>
    /// <returns>The start timestamp, or zero.</returns>
    [NonEvent]
    public long RecoveryStart(Storage storage)
    {
        if (!IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            return 0;
        }

        long started = Stopwatch.GetTimestamp();
        RecoveryStart(storage.Name.ToString(), storage.Id.ToString());
        return started;
    }

    /// <summary>
    /// Writes that a file set's recovery completed: the journal was replayed and the pages scanned.
    /// Nothing is written when <paramref name="started"/> is zero (the start was not written).
    /// </summary>
    /// <param name="storage">The storage being opened.</param>
    /// <param name="rebuiltPages">The pages recovery rebuilt from the journal.</param>
    /// <param name="maxSequence">The highest transaction sequence the journal held.</param>
    /// <param name="redoLsn">The redo point the open settled on.</param>
    /// <param name="started">The timestamp <see cref="RecoveryStart(Storage)"/> returned.</param>
    [NonEvent]
    public void RecoveryStop(Storage storage, int rebuiltPages, long maxSequence, long redoLsn, long started)
    {
        if (started != 0 && IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            RecoveryStop(storage.Name.ToString(), rebuiltPages, maxSequence, redoLsn, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    /// <summary>
    /// Writes that a page outlived the journal records that stamped it (a lost journal tail, or a
    /// journal older than the data file): the open moved the LSN floor and the redo point up to it.
    /// </summary>
    /// <param name="storage">The storage being opened.</param>
    /// <param name="strayLsn">The highest LSN a page carries above the redo point.</param>
    /// <param name="redoLsn">The redo point before the open moved it.</param>
    [NonEvent]
    public void JournalTailLost(Storage storage, long strayLsn, long redoLsn)
    {
        if (IsEnabled(EventLevel.Warning, EventKeywords.None))
        {
            JournalTailLost(storage.Name.ToString(), strayLsn, redoLsn);
        }
    }

    /// <summary>
    /// Writes that a checkpoint started, and returns the timestamp <see cref="CheckpointStop"/>
    /// measures from, or zero when the event is not enabled.
    /// </summary>
    /// <param name="storage">The storage.</param>
    /// <param name="activeTransactions">The logical transactions in flight the checkpoint anchors.</param>
    /// <returns>The start timestamp, or zero.</returns>
    [NonEvent]
    public long CheckpointStart(Storage storage, int activeTransactions)
    {
        if (!IsEnabled(EventLevel.Informational, Keywords.Checkpoints))
        {
            return 0;
        }

        long started = Stopwatch.GetTimestamp();
        CheckpointStart(storage.Name.ToString(), activeTransactions, storage.JournalLength);
        return started;
    }

    /// <summary>
    /// Writes that a checkpoint ended. A checkpoint that failed writes it too, with
    /// <paramref name="checkpointLsn"/> zero, so its activity closes. Nothing is written when
    /// <paramref name="started"/> is zero (the start was not written).
    /// </summary>
    /// <param name="storage">The storage.</param>
    /// <param name="checkpointLsn">The checkpoint record's LSN; zero when the checkpoint failed.</param>
    /// <param name="started">The timestamp <see cref="CheckpointStart(Storage, int)"/> returned.</param>
    [NonEvent]
    public void CheckpointStop(Storage storage, long checkpointLsn, long started)
    {
        if (started != 0 && IsEnabled(EventLevel.Informational, Keywords.Checkpoints))
        {
            CheckpointStop(storage.Name.ToString(), checkpointLsn, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    /// <summary>
    /// Returns the timestamp a paced write-back pass measures from when
    /// <see cref="PagesWrittenBack"/> is enabled, and zero otherwise.
    /// </summary>
    /// <returns>The start timestamp, or zero.</returns>
    [NonEvent]
    public long WriteBackStarting()
        => IsEnabled(EventLevel.Verbose, Keywords.WriteBack) ? Stopwatch.GetTimestamp() : 0;

    /// <summary>
    /// Writes a paced write-back pass that wrote at least one page. Nothing is written when
    /// <paramref name="started"/> is zero or no page was written.
    /// </summary>
    /// <param name="storage">The storage.</param>
    /// <param name="pages">The pages the pass wrote.</param>
    /// <param name="started">The timestamp <see cref="WriteBackStarting"/> returned.</param>
    [NonEvent]
    public void PagesWrittenBack(Storage storage, int pages, long started)
    {
        if (pages > 0 && started != 0 && IsEnabled(EventLevel.Verbose, Keywords.WriteBack))
        {
            PagesWrittenBack(storage.Name.ToString(), pages, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    /// <summary>Writes that a flush worker's group flush made the pending commits durable.</summary>
    /// <param name="gate">The storage's group-commit gate.</param>
    /// <param name="durableLsn">The journal's durable LSN after the flush.</param>
    [NonEvent]
    public void PendingCommitsFlushed(StorageGroupCommitGate gate, long durableLsn)
    {
        if (IsEnabled(EventLevel.Verbose, Keywords.GroupCommit))
        {
            PendingCommitsFlushed(gate.StorageName, durableLsn);
        }
    }

    /// <summary>
    /// Writes that a grouped commit's window passed before the flush worker flushed it, so the
    /// committer flushed the journal inline itself.
    /// </summary>
    /// <param name="gate">The storage's group-commit gate.</param>
    /// <param name="lsn">The commit's LSN.</param>
    /// <param name="window">The window the commit waited.</param>
    [NonEvent]
    public void GroupCommitWindowMissed(StorageGroupCommitGate gate, long lsn, TimeSpan window)
    {
        if (IsEnabled(EventLevel.Verbose, Keywords.GroupCommit))
        {
            GroupCommitWindowMissed(gate.StorageName, lsn, window.TotalMilliseconds);
        }
    }

    /// <summary>
    /// Writes that a storage went offline: a device operation failed, another file set of the
    /// database went offline, or its engine gave up on it.
    /// </summary>
    /// <param name="storage">The storage.</param>
    /// <param name="error">The error that took it offline; its inner exception is the failure written.</param>
    [NonEvent]
    public void StorageOffline(Storage storage, StorageOfflineException error)
    {
        if (IsEnabled(EventLevel.Error, EventKeywords.None))
        {
            var failure = error.InnerException ?? error;
            StorageOffline(storage.Name.ToString(), error.Cause.ToString(), failure.GetType().FullName ?? failure.GetType().Name, failure.Message);
        }
    }

    /// <summary>
    /// Writes that a storage transaction's commit record is in the journal but the wait for its
    /// durability failed: the bracket ended committed in memory, and the reopen's recovery decides
    /// whether it survives.
    /// </summary>
    /// <param name="storage">The storage.</param>
    /// <param name="transactionSequence">The storage transaction's sequence.</param>
    /// <param name="commitLsn">The commit record's LSN.</param>
    /// <param name="error">The failure of the wait.</param>
    [NonEvent]
    public void StorageCommitUnconfirmed(Storage storage, long transactionSequence, long commitLsn, Exception error)
    {
        if (IsEnabled(EventLevel.Error, EventKeywords.None))
        {
            StorageCommitUnconfirmed(storage.Name.ToString(), transactionSequence, commitLsn, error.Message);
        }
    }

    /// <summary>
    /// Writes that closing an offline storage wrote nothing: no checkpoint and no header, so the
    /// reopen's recovery decides every unconfirmed commit.
    /// </summary>
    /// <param name="storage">The storage.</param>
    /// <param name="error">The error that took it offline.</param>
    [NonEvent]
    public void ShutdownFlushSkipped(Storage storage, StorageOfflineException? error)
    {
        if (IsEnabled(EventLevel.Warning, EventKeywords.None))
        {
            ShutdownFlushSkipped(storage.Name.ToString(), error is null ? "Offline" : "Offline: " + error.Cause.ToString());
        }
    }

    /// <summary>Writes that a buffer pool refused a page because every resident page is pinned.</summary>
    /// <param name="pool">The buffer pool.</param>
    /// <param name="capacity">The pool's capacity in pages.</param>
    [NonEvent]
    public void BufferPoolExhausted(StorageBufferPool pool, int capacity)
    {
        if (IsEnabled(EventLevel.Error, EventKeywords.None))
        {
            BufferPoolExhausted(pool.StorageName, capacity);
        }
    }

    /// <summary>Writes that an eviction wrote a dirty page back on the evicting thread.</summary>
    /// <param name="pool">The buffer pool.</param>
    /// <param name="pageId">The page written back.</param>
    /// <param name="pageLsn">The LSN the written image carries.</param>
    [NonEvent]
    public void DirtyPageEvicted(StorageBufferPool pool, PageId pageId, long pageLsn)
    {
        if (IsEnabled(EventLevel.Verbose, Keywords.BufferPool))
        {
            DirtyPageEvicted(pool.StorageName, (long)pageId, pageLsn);
        }
    }

    /// <summary>Writes that a buffer pool's capacity changed.</summary>
    /// <param name="pool">The buffer pool.</param>
    /// <param name="oldCapacity">The capacity before, in pages.</param>
    /// <param name="newCapacity">The capacity now, in pages.</param>
    [NonEvent]
    public void BufferPoolResized(StorageBufferPool pool, int oldCapacity, int newCapacity)
    {
        if (IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            BufferPoolResized(pool.StorageName, oldCapacity, newCapacity);
        }
    }

    /// <summary>Writes that a page read from the data file failed its checksum.</summary>
    /// <param name="pool">The buffer pool that read it.</param>
    /// <param name="pageId">The page.</param>
    [NonEvent]
    public void PageChecksumFailed(StorageBufferPool pool, PageId pageId)
    {
        if (IsEnabled(EventLevel.Error, EventKeywords.None))
        {
            PageChecksumFailed(pool.StorageName, (long)pageId);
        }
    }

    /// <summary>
    /// Returns the timestamp a storage's close measures from when <see cref="StorageClosed"/> is
    /// enabled, and zero otherwise.
    /// </summary>
    /// <returns>The start timestamp, or zero.</returns>
    [NonEvent]
    public long StorageClosing()
        => IsEnabled(EventLevel.Informational, EventKeywords.None) ? Stopwatch.GetTimestamp() : 0;

    /// <summary>
    /// Writes that a storage was closed. Nothing is written when <paramref name="started"/> is zero.
    /// </summary>
    /// <param name="storage">The storage.</param>
    /// <param name="started">The timestamp <see cref="StorageClosing"/> returned.</param>
    [NonEvent]
    public void StorageClosed(Storage storage, long started)
    {
        if (started != 0 && IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            StorageClosed(storage.Name.ToString(), Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    [Event(1, Level = EventLevel.Informational, Message = "Storage '{0}' ({1}, {2}) created a new file set.")]
    private void StorageCreated(string database, string storageId, string model)
        => WriteEvent(1, database, storageId, model);

    [Event(2, Level = EventLevel.Informational, Message = "Storage '{0}' ({1}) started recovery: replaying its journal.")]
    private void RecoveryStart(string database, string storageId)
        => WriteEvent(2, database, storageId);

    [Event(3, Level = EventLevel.Informational, Message = "Storage '{0}' recovered: {1} page(s) rebuilt from the journal, highest sequence {2}, redo point {3}, in {4} ms.")]
    private void RecoveryStop(string database, int rebuiltPages, long maxSequence, long redoLsn, double durationMilliseconds)
        => WriteEvent(3, database, rebuiltPages, maxSequence, redoLsn, durationMilliseconds);

    [Event(4, Level = EventLevel.Warning, Message = "Storage '{0}' found a page at LSN {1}, above the journal's redo point {2}: the journal lost the records that stamped it. LSNs resume above the page, and its next change journals a full image.")]
    private void JournalTailLost(string database, long strayLsn, long redoLsn)
        => WriteEvent(4, database, strayLsn, redoLsn);

    [Event(5, Level = EventLevel.Informational, Keywords = Keywords.Checkpoints, Message = "Storage '{0}' checkpoint started: {1} transaction(s) in flight, {2} journal byte(s).")]
    private void CheckpointStart(string database, int activeTransactions, long journalLength)
        => WriteEvent(5, database, activeTransactions, journalLength);

    [Event(6, Level = EventLevel.Informational, Keywords = Keywords.Checkpoints, Message = "Storage '{0}' checkpoint ended at LSN {1} in {2} ms.")]
    private void CheckpointStop(string database, long checkpointLsn, double durationMilliseconds)
        => WriteEvent(6, database, checkpointLsn, durationMilliseconds);

    [Event(7, Level = EventLevel.Verbose, Keywords = Keywords.WriteBack, Message = "Storage '{0}' wrote back {1} dirty page(s) in {2} ms.")]
    private void PagesWrittenBack(string database, int pages, double durationMilliseconds)
        => WriteEvent(7, database, pages, durationMilliseconds);

    [Event(8, Level = EventLevel.Verbose, Keywords = Keywords.GroupCommit, Message = "Storage '{0}' group flush made the journal durable to LSN {1}.")]
    private void PendingCommitsFlushed(string database, long durableLsn)
        => WriteEvent(8, database, durableLsn);

    [Event(9, Level = EventLevel.Verbose, Keywords = Keywords.GroupCommit, Message = "Storage '{0}' grouped commit at LSN {1} waited its {2} ms window and flushed the journal inline.")]
    private void GroupCommitWindowMissed(string database, long lsn, double windowMilliseconds)
        => WriteEvent(9, database, lsn, windowMilliseconds);

    [Event(10, Level = EventLevel.Error, Message = "Storage '{0}' went offline ({1}): {2}: {3}. Nothing more is written to its journal or data file until it is reopened.")]
    private void StorageOffline(string database, string cause, string exceptionType, string exceptionMessage)
        => WriteEvent(10, database, cause, exceptionType, exceptionMessage);

    [Event(11, Level = EventLevel.Error, Message = "Storage '{0}' transaction {1} has its commit record at LSN {2} in the journal, but its durability was not confirmed: {3}. The reopen's recovery decides whether it survives.")]
    private void StorageCommitUnconfirmed(string database, long transactionSequence, long commitLsn, string exceptionMessage)
        => WriteEvent(11, database, transactionSequence, commitLsn, exceptionMessage);

    [Event(12, Level = EventLevel.Warning, Message = "Storage '{0}' closed without its shutdown flush ({1}); the reopen's recovery reads the journal.")]
    private void ShutdownFlushSkipped(string database, string reason)
        => WriteEvent(12, database, reason);

    [Event(13, Level = EventLevel.Error, Message = "Storage '{0}' buffer pool is full: all {1} page(s) are pinned.")]
    private void BufferPoolExhausted(string database, int capacity)
        => WriteEvent(13, database, capacity);

    [Event(14, Level = EventLevel.Verbose, Keywords = Keywords.BufferPool, Message = "Storage '{0}' evicted dirty page {1} (LSN {2}) and wrote it back on the evicting thread.")]
    private void DirtyPageEvicted(string database, long pageId, long pageLsn)
        => WriteEvent(14, database, pageId, pageLsn);

    [Event(15, Level = EventLevel.Informational, Message = "Storage '{0}' buffer pool resized from {1} to {2} page(s).")]
    private void BufferPoolResized(string database, int oldCapacity, int newCapacity)
        => WriteEvent(15, database, oldCapacity, newCapacity);

    [Event(16, Level = EventLevel.Error, Message = "Storage '{0}' page {1} failed its checksum when it was read.")]
    private void PageChecksumFailed(string database, long pageId)
        => WriteEvent(16, database, pageId);

    [Event(17, Level = EventLevel.Informational, Message = "Storage '{0}' closed in {1} ms.")]
    private void StorageClosed(string database, double durationMilliseconds)
        => WriteEvent(17, database, durationMilliseconds);

    /// <inheritdoc />
    protected override void OnEventCommand(EventCommandEventArgs command)
    {
        if (command.Command != EventCommand.Enable)
        {
            return;
        }

        // Created on the first enable command, as the runtime's own sources do, and kept for the
        // source's lifetime. The backing fields are maintained whether or not anyone listens, so a
        // tool that attaches late still reads exact values.
        _currentStoragesCounter ??= new PollingCounter("current-storages", this, () => Volatile.Read(ref _currentStorages))
        {
            DisplayName = "Current Storages",
        };
        _checkpointRateCounter ??= new IncrementingPollingCounter("checkpoints-per-second", this, () => Volatile.Read(ref _checkpoints))
        {
            DisplayName = "Checkpoint Rate",
            DisplayRateTimeScale = TimeSpan.FromSeconds(1),
        };
        _journalFlushRateCounter ??= new IncrementingPollingCounter("journal-flushes-per-second", this, () => Volatile.Read(ref _journalFlushes))
        {
            DisplayName = "Durable Journal Flush Rate",
            DisplayRateTimeScale = TimeSpan.FromSeconds(1),
        };
        _pageReadRateCounter ??= new IncrementingPollingCounter("page-reads-per-second", this, () => Volatile.Read(ref _pageReads))
        {
            DisplayName = "Page Read Rate",
            DisplayRateTimeScale = TimeSpan.FromSeconds(1),
        };
        _pageWriteRateCounter ??= new IncrementingPollingCounter("page-writes-per-second", this, () => Volatile.Read(ref _pageWrites))
        {
            DisplayName = "Page Write Rate",
            DisplayRateTimeScale = TimeSpan.FromSeconds(1),
        };
        _foregroundPageWriteRateCounter ??= new IncrementingPollingCounter("foreground-page-writes-per-second", this, () => Volatile.Read(ref _foregroundPageWrites))
        {
            DisplayName = "Foreground Page Write Rate",
            DisplayRateTimeScale = TimeSpan.FromSeconds(1),
        };
        _groupCommitSelfFlushRateCounter ??= new IncrementingPollingCounter("group-commit-self-flushes-per-second", this, () => Volatile.Read(ref _groupCommitSelfFlushes))
        {
            DisplayName = "Group Commit Self-Flush Rate",
            DisplayRateTimeScale = TimeSpan.FromSeconds(1),
        };
        _preImageSpillRateCounter ??= new IncrementingPollingCounter("pre-images-spilled-per-second", this, () => Volatile.Read(ref _preImagesSpilled))
        {
            DisplayName = "Pre-Image Spill Rate",
            DisplayRateTimeScale = TimeSpan.FromSeconds(1),
        };
    }
}
