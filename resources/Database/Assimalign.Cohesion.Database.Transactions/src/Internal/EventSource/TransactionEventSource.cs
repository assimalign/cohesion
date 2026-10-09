using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Threading;

using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Transactions.Internal;

/// <summary>
/// The transaction kernel's diagnostics: the lifecycle of a database's logical transactions, the
/// lock manager's waits and deadlocks, deferred undo, open-time recovery analysis, deferred
/// checkpoints and version-purge passes, and the kernel's transaction and lock counters.
/// </summary>
/// <remarks>
/// <para>
/// Internal by the repository's EventSource convention (<c>.claude/rules/event-source.md</c>). Tools
/// enable it by its assembly name, <c>Assimalign.Cohesion.Database.Transactions</c>; applications
/// forward it into their logging with <c>Assimalign.Cohesion.Logging.EventSource</c>. Every engine
/// model composes its database's kernel through <see cref="TransactionCoordinator"/>, so this one
/// source covers the transactions of all five engines. The <c>database</c> payload is the storage's
/// name, which the coordinator hands to the manager and the lock manager it builds; a standalone
/// <see cref="TransactionManager.Create(LockManager, VersionStore, Func{TransactionSequence})"/> or
/// <see cref="LockManager.Create"/> reports an empty one.
/// </para>
/// <para>
/// A lock wait that lasts at least <see cref="SlowLockWaitThresholdMilliseconds"/> also writes
/// <c>SlowLockWait</c>, PostgreSQL's <c>log_lock_waits</c> (<c>src/backend/storage/lmgr/proc.c:1700</c>).
/// The threshold is the <c>SlowLockWaitThresholdMs</c> argument of the last session that enabled
/// the source, 1000 ms when that session passed none (the in-process forwarder passes none).
/// </para>
/// </remarks>
[EventSource(Name = "Assimalign.Cohesion.Database.Transactions")]
internal sealed class TransactionEventSource : EventSource
{
    /// <summary>The name of the event-source argument that sets the slow-lock-wait threshold, in milliseconds.</summary>
    internal const string SlowLockWaitThresholdArgument = "SlowLockWaitThresholdMs";

    /// <summary>The slow-lock-wait threshold of a session that passes no argument.</summary>
    internal const double DefaultSlowLockWaitThresholdMilliseconds = 1000;

    public static readonly TransactionEventSource Log = new();

    private PollingCounter? _currentTransactionsCounter;
    private IncrementingPollingCounter? _transactionRateCounter;
    private IncrementingPollingCounter? _commitRateCounter;
    private IncrementingPollingCounter? _rollbackRateCounter;
    private PollingCounter? _totalDeadlocksCounter;
    private IncrementingPollingCounter? _lockWaitRateCounter;
    private long _currentTransactions;
    private long _totalTransactions;
    private long _totalCommits;
    private long _totalRollbacks;
    private long _totalDeadlocks;
    private long _totalLockWaits;
    private double _slowLockWaitThresholdMilliseconds = DefaultSlowLockWaitThresholdMilliseconds;

    private TransactionEventSource()
    {
    }

    /// <summary>
    /// The keywords of the high-volume <see cref="EventLevel.Verbose"/> families, so a tool can take
    /// one family without the others.
    /// </summary>
    public static class Keywords
    {
        /// <summary>Transaction begin, commit and rollback.</summary>
        public const EventKeywords Transactions = (EventKeywords)0x1;

        /// <summary>Lock waits.</summary>
        public const EventKeywords Locks = (EventKeywords)0x2;

        /// <summary>Checkpoints deferred to a statement, and the deferred checkpoints skipped.</summary>
        public const EventKeywords Checkpoints = (EventKeywords)0x4;

        /// <summary>Version-purge passes.</summary>
        public const EventKeywords Purge = (EventKeywords)0x8;
    }

    /// <summary>The coordinator transactions begun and not yet ended.</summary>
    internal long CurrentTransactions => Volatile.Read(ref _currentTransactions);

    /// <summary>The coordinator transactions begun since the process started.</summary>
    internal long TotalTransactions => Volatile.Read(ref _totalTransactions);

    /// <summary>The coordinator transactions that ended committed since the process started.</summary>
    internal long TotalCommits => Volatile.Read(ref _totalCommits);

    /// <summary>The coordinator transactions that ended rolled back or aborted since the process started.</summary>
    internal long TotalRollbacks => Volatile.Read(ref _totalRollbacks);

    /// <summary>The deadlock victims chosen since the process started.</summary>
    internal long TotalDeadlocks => Volatile.Read(ref _totalDeadlocks);

    /// <summary>The lock requests that had to wait since the process started.</summary>
    internal long TotalLockWaits => Volatile.Read(ref _totalLockWaits);

    /// <summary>The duration, in milliseconds, from which a lock wait also writes <c>SlowLockWait</c>.</summary>
    internal double SlowLockWaitThresholdMilliseconds => Volatile.Read(ref _slowLockWaitThresholdMilliseconds);

    /// <summary>
    /// Counts a transaction the coordinator began tracking: the opening transition of the
    /// <c>current-transactions</c> gauge.
    /// </summary>
    [NonEvent]
    public void TransactionTracked()
    {
        Interlocked.Increment(ref _currentTransactions);
        Interlocked.Increment(ref _totalTransactions);
    }

    /// <summary>
    /// Counts a transaction the coordinator stopped tracking because it ended: the closing
    /// transition of the <c>current-transactions</c> gauge.
    /// </summary>
    /// <param name="committed">True when the transaction ended committed; otherwise it rolled back or was aborted.</param>
    [NonEvent]
    public void TransactionUntracked(bool committed)
    {
        Interlocked.Decrement(ref _currentTransactions);

        if (committed)
        {
            Interlocked.Increment(ref _totalCommits);
        }
        else
        {
            Interlocked.Increment(ref _totalRollbacks);
        }
    }

    /// <summary>Writes that a database began a transaction.</summary>
    [NonEvent]
    public void TransactionBegun(Storage.Storage storage, TransactionContext context)
    {
        if (IsEnabled(EventLevel.Verbose, Keywords.Transactions))
        {
            TransactionBegun(storage.Name, (long)context.Sequence.Value, context.IsolationLevel.ToString());
        }
    }

    /// <summary>Writes that a database committed a transaction.</summary>
    [NonEvent]
    public void TransactionCommitted(Storage.Storage storage, TransactionContext context)
    {
        if (IsEnabled(EventLevel.Verbose, Keywords.Transactions))
        {
            TransactionCommitted(storage.Name, (long)context.Sequence.Value);
        }
    }

    /// <summary>Writes that a database rolled a transaction back.</summary>
    [NonEvent]
    public void TransactionRolledBack(Storage.Storage storage, TransactionContext context)
    {
        if (IsEnabled(EventLevel.Verbose, Keywords.Transactions))
        {
            TransactionRolledBack(storage.Name, (long)context.Sequence.Value);
        }
    }

    /// <summary>
    /// Writes that the kernel rolled a transaction back because its commit record could not be
    /// written; the caller's commit fails with <see cref="TransactionAbortedException"/>.
    /// </summary>
    [NonEvent]
    public void CommitRecordWriteFailed(string database, TransactionSequence sequence, Exception exception)
    {
        if (IsEnabled(EventLevel.Warning, EventKeywords.None))
        {
            CommitRecordWriteFailed(database, (long)sequence.Value, TypeName(exception), exception.Message);
        }
    }

    /// <summary>
    /// Writes that a transaction committed but its commit record could not be made durable. The
    /// message is the flush failure's (the exception's inner exception), when there is one.
    /// </summary>
    [NonEvent]
    public void CommitUnconfirmed(Storage.Storage storage, TransactionContext context, TransactionCommitUnconfirmedException exception)
    {
        if (IsEnabled(EventLevel.Error, EventKeywords.None))
        {
            CommitUnconfirmed(storage.Name, (long)context.Sequence.Value, (exception.InnerException ?? exception).Message);
        }
    }

    /// <summary>
    /// Counts and writes a deadlock: the request of <paramref name="owner"/> would have closed a
    /// cycle, so it was chosen as the victim.
    /// </summary>
    [NonEvent]
    public void DeadlockDetected(string database, TransactionSequence owner, LockResource resource, LockMode mode)
    {
        Interlocked.Increment(ref _totalDeadlocks);

        if (IsEnabled(EventLevel.Warning, EventKeywords.None))
        {
            DeadlockDetected(database, (long)owner.Value, DescribeResource(resource), mode.ToString());
        }
    }

    /// <summary>Counts and writes the start of a lock request's wait.</summary>
    [NonEvent]
    public void LockWaitStart(string database, TransactionSequence owner, LockResource resource, LockMode mode)
    {
        Interlocked.Increment(ref _totalLockWaits);

        if (IsEnabled(EventLevel.Verbose, Keywords.Locks))
        {
            LockWaitStart(database, (long)owner.Value, DescribeResource(resource), mode.ToString());
        }
    }

    /// <summary>
    /// Writes the end of a lock request's wait, and <c>SlowLockWait</c> first when the wait lasted
    /// at least <see cref="SlowLockWaitThresholdMilliseconds"/>.
    /// </summary>
    /// <param name="database">The database whose lock manager the request waited in.</param>
    /// <param name="owner">The waiting transaction.</param>
    /// <param name="resource">The resource it waited for.</param>
    /// <param name="mode">The mode it requested.</param>
    /// <param name="outcome">How the wait ended: <c>Granted</c>, <c>Cancelled</c>, <c>Ended</c> or <c>Abandoned</c>.</param>
    /// <param name="startTimestamp">The <see cref="Stopwatch.GetTimestamp"/> taken when the wait began.</param>
    [NonEvent]
    public void LockWaitStop(string database, TransactionSequence owner, LockResource resource, LockMode mode, string outcome, long startTimestamp)
    {
        bool stop = IsEnabled(EventLevel.Verbose, Keywords.Locks);
        bool slow = IsEnabled(EventLevel.Warning, EventKeywords.None);
        if (!stop && !slow)
        {
            return;
        }

        double duration = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        double threshold = SlowLockWaitThresholdMilliseconds;

        // Written before the stop, so a tool that tracks the wait as an activity sees it inside.
        if (slow && duration >= threshold)
        {
            SlowLockWait(database, (long)owner.Value, DescribeResource(resource), mode.ToString(), duration, threshold);
        }

        if (stop)
        {
            LockWaitStop(database, (long)owner.Value, outcome, duration);
        }
    }

    /// <summary>Writes that a lock manager failed every wait because its database's storage went offline.</summary>
    [NonEvent]
    public void LockWaitsAbandoned(string database, StorageOfflineException cause)
    {
        if (IsEnabled(EventLevel.Warning, EventKeywords.None))
        {
            LockWaitsAbandoned(database, cause.Cause.ToString());
        }
    }

    /// <summary>
    /// Writes that a rollback's undo failed, so the transaction ended with its undo deferred: the
    /// writer keeps its locks until a retry completes it.
    /// </summary>
    [NonEvent]
    public void UndoDeferred(string database, TransactionSequence writer, Exception exception)
    {
        if (IsEnabled(EventLevel.Warning, EventKeywords.None))
        {
            UndoDeferred(database, (long)writer.Value, TypeName(exception), exception.Message);
        }
    }

    /// <summary>Writes that a retry completed a deferred undo and released the writer.</summary>
    [NonEvent]
    public void DeferredUndoCompleted(string database, TransactionSequence writer, long undone)
    {
        if (IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            DeferredUndoCompleted(database, (long)writer.Value, undone);
        }
    }

    /// <summary>Writes that a transaction's advisory abort record could not be written.</summary>
    [NonEvent]
    public void AbortRecordWriteFailed(string database, TransactionSequence sequence, Exception exception)
    {
        if (IsEnabled(EventLevel.Warning, EventKeywords.None))
        {
            AbortRecordWriteFailed(database, (long)sequence.Value, TypeName(exception), exception.Message);
        }
    }

    /// <summary>Writes the classification open-time recovery made of the recovered journal.</summary>
    [NonEvent]
    public void RecoveryAnalyzed(Storage.Storage storage, TransactionRecoveryPlan plan)
    {
        if (IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            RecoveryAnalyzed(storage.Name, plan.Committed.Count, plan.Aborted.Count, (long)plan.MaxSequence.Value);
        }
    }

    /// <summary>Writes that a checkpoint was deferred to the statement holding the apply gate.</summary>
    [NonEvent]
    public void CheckpointDeferred(Storage.Storage storage)
    {
        if (IsEnabled(EventLevel.Verbose, Keywords.Checkpoints))
        {
            CheckpointDeferred(storage.Name);
        }
    }

    /// <summary>Writes that a deferred checkpoint did not run, and why.</summary>
    /// <param name="storage">The database's storage.</param>
    /// <param name="reason"><c>BracketOpen</c> (the request stays) or <c>StorageOffline</c> (the request is dropped).</param>
    [NonEvent]
    public void DeferredCheckpointSkipped(Storage.Storage storage, string reason)
    {
        if (IsEnabled(EventLevel.Verbose, Keywords.Checkpoints))
        {
            DeferredCheckpointSkipped(storage.Name, reason);
        }
    }

    /// <summary>Writes that a deferred checkpoint failed; the next checkpoint request throws the failure.</summary>
    [NonEvent]
    public void DeferredCheckpointFailed(Storage.Storage storage, Exception exception)
    {
        if (IsEnabled(EventLevel.Warning, EventKeywords.None))
        {
            DeferredCheckpointFailed(storage.Name, TypeName(exception), exception.Message);
        }
    }

    /// <summary>Writes one completed version-purge pass.</summary>
    /// <param name="storage">The database's storage.</param>
    /// <param name="versionsPurged">The versions and index entries the pass reclaimed or undid.</param>
    /// <param name="startTimestamp">The <see cref="Stopwatch.GetTimestamp"/> taken when the pass began.</param>
    [NonEvent]
    public void VersionPurgePass(Storage.Storage storage, long versionsPurged, long startTimestamp)
    {
        if (IsEnabled(EventLevel.Verbose, Keywords.Purge))
        {
            VersionPurgePass(storage.Name, versionsPurged, Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);
        }
    }

    [Event(1, Level = EventLevel.Verbose, Keywords = Keywords.Transactions, Message = "Database '{0}' began transaction {1} ({2}).")]
    private void TransactionBegun(string database, long transactionSequence, string isolationLevel)
        => WriteEvent(1, database, transactionSequence, isolationLevel);

    [Event(2, Level = EventLevel.Verbose, Keywords = Keywords.Transactions, Message = "Database '{0}' committed transaction {1}.")]
    private void TransactionCommitted(string database, long transactionSequence)
        => WriteEvent(2, database, transactionSequence);

    [Event(3, Level = EventLevel.Verbose, Keywords = Keywords.Transactions, Message = "Database '{0}' rolled back transaction {1}.")]
    private void TransactionRolledBack(string database, long transactionSequence)
        => WriteEvent(3, database, transactionSequence);

    [Event(4, Level = EventLevel.Warning, Message = "Database '{0}' aborted transaction {1}: its commit record could not be written ({2}: {3}), so the transaction was rolled back.")]
    private void CommitRecordWriteFailed(string database, long transactionSequence, string exceptionType, string exceptionMessage)
        => WriteEvent(4, database, transactionSequence, exceptionType, exceptionMessage);

    [Event(5, Level = EventLevel.Error, Message = "Database '{0}' committed transaction {1}, but its commit record could not be made durable: {2}. The storage is offline; the reopen's recovery decides the outcome.")]
    private void CommitUnconfirmed(string database, long transactionSequence, string exceptionMessage)
        => WriteEvent(5, database, transactionSequence, exceptionMessage);

    [Event(6, Level = EventLevel.Warning, Message = "Database '{0}' chose transaction {1} as the deadlock victim requesting {3} on {2}.")]
    private void DeadlockDetected(string database, long transactionSequence, string resource, string mode)
        => WriteEvent(6, database, transactionSequence, resource, mode);

    [Event(7, Level = EventLevel.Verbose, Keywords = Keywords.Locks, Message = "Transaction {1} of database '{0}' waits for {3} on {2}.")]
    private void LockWaitStart(string database, long transactionSequence, string resource, string mode)
        => WriteEvent(7, database, transactionSequence, resource, mode);

    [Event(8, Level = EventLevel.Verbose, Keywords = Keywords.Locks, Message = "Transaction {1} of database '{0}' stopped waiting for a lock: {2} after {3} ms.")]
    private void LockWaitStop(string database, long transactionSequence, string outcome, double durationMilliseconds)
        => WriteEvent(8, database, transactionSequence, outcome, durationMilliseconds);

    [Event(9, Level = EventLevel.Warning, Message = "Transaction {1} of database '{0}' waited {4} ms for {3} on {2}, at least the {5} ms slow-lock-wait threshold.")]
    private void SlowLockWait(string database, long transactionSequence, string resource, string mode, double durationMilliseconds, double thresholdMilliseconds)
        => WriteEvent(9, database, transactionSequence, resource, mode, durationMilliseconds, thresholdMilliseconds);

    [Event(10, Level = EventLevel.Warning, Message = "Database '{0}' failed every lock wait because its storage went offline ({1}); no lock of it is released until it is reopened.")]
    private void LockWaitsAbandoned(string database, string cause)
        => WriteEvent(10, database, cause);

    [Event(11, Level = EventLevel.Warning, Message = "Database '{0}' deferred the undo of transaction {1}: {2}: {3}. The writer keeps its locks until a retry completes the undo.")]
    private void UndoDeferred(string database, long transactionSequence, string exceptionType, string exceptionMessage)
        => WriteEvent(11, database, transactionSequence, exceptionType, exceptionMessage);

    [Event(12, Level = EventLevel.Informational, Message = "Database '{0}' completed the deferred undo of transaction {1} ({2} versions and index entries) and released its locks.")]
    private void DeferredUndoCompleted(string database, long transactionSequence, long undone)
        => WriteEvent(12, database, transactionSequence, undone);

    [Event(13, Level = EventLevel.Warning, Message = "Database '{0}' could not write the abort record of transaction {1}: {2}: {3}. Recovery classifies the transaction as aborted without it.")]
    private void AbortRecordWriteFailed(string database, long transactionSequence, string exceptionType, string exceptionMessage)
        => WriteEvent(13, database, transactionSequence, exceptionType, exceptionMessage);

    [Event(14, Level = EventLevel.Informational, Message = "Database '{0}' recovery classified {1} committed and {2} aborted transactions; the highest sequence is {3}.")]
    private void RecoveryAnalyzed(string database, int committed, int aborted, long maxSequence)
        => WriteEvent(14, database, committed, aborted, maxSequence);

    [Event(15, Level = EventLevel.Verbose, Keywords = Keywords.Checkpoints, Message = "Database '{0}' deferred a checkpoint to the statement holding its apply gate.")]
    private void CheckpointDeferred(string database)
        => WriteEvent(15, database);

    [Event(16, Level = EventLevel.Verbose, Keywords = Keywords.Checkpoints, Message = "Database '{0}' skipped a deferred checkpoint: {1}.")]
    private void DeferredCheckpointSkipped(string database, string reason)
        => WriteEvent(16, database, reason);

    [Event(17, Level = EventLevel.Warning, Message = "Database '{0}' failed a deferred checkpoint: {1}: {2}. The next checkpoint request throws the failure.")]
    private void DeferredCheckpointFailed(string database, string exceptionType, string exceptionMessage)
        => WriteEvent(17, database, exceptionType, exceptionMessage);

    [Event(18, Level = EventLevel.Verbose, Keywords = Keywords.Purge, Message = "Database '{0}' version-purge pass reclaimed or undid {1} versions in {2} ms.")]
    private void VersionPurgePass(string database, long versionsPurged, double durationMilliseconds)
        => WriteEvent(18, database, versionsPurged, durationMilliseconds);

    /// <inheritdoc />
    protected override void OnEventCommand(EventCommandEventArgs command)
    {
        if (command.Command == EventCommand.Disable)
        {
            // A session that ends takes its threshold with it, as the root source's does: a brief
            // tool session that set 0 ms must not leave a forwarder that is still enabled at
            // Warning reporting every lock wait as slow.
            Volatile.Write(ref _slowLockWaitThresholdMilliseconds, DefaultSlowLockWaitThresholdMilliseconds);
            return;
        }

        if (command.Command != EventCommand.Enable)
        {
            return;
        }

        // The last session that enables the source sets the threshold; one that passes no
        // argument (the in-process forwarder), or one that does not parse as a finite,
        // non-negative number, sets the default.
        double threshold = DefaultSlowLockWaitThresholdMilliseconds;
        if (command.Arguments is { } arguments
            && arguments.TryGetValue(SlowLockWaitThresholdArgument, out string? value)
            && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            && double.IsFinite(parsed)
            && parsed >= 0)
        {
            threshold = parsed;
        }

        Volatile.Write(ref _slowLockWaitThresholdMilliseconds, threshold);

        // Created on the first enable command, as the runtime's own sources do, and kept for the
        // source's lifetime. The backing fields are maintained whether or not anyone listens, so a
        // tool that attaches late still reads exact values.
        _currentTransactionsCounter ??= new PollingCounter("current-transactions", this, () => Volatile.Read(ref _currentTransactions))
        {
            DisplayName = "Current Transactions",
        };
        _transactionRateCounter ??= new IncrementingPollingCounter("transactions-per-second", this, () => Volatile.Read(ref _totalTransactions))
        {
            DisplayName = "Transaction Rate",
            DisplayRateTimeScale = TimeSpan.FromSeconds(1),
        };
        _commitRateCounter ??= new IncrementingPollingCounter("commits-per-second", this, () => Volatile.Read(ref _totalCommits))
        {
            DisplayName = "Commit Rate",
            DisplayRateTimeScale = TimeSpan.FromSeconds(1),
        };
        _rollbackRateCounter ??= new IncrementingPollingCounter("rollbacks-per-second", this, () => Volatile.Read(ref _totalRollbacks))
        {
            DisplayName = "Rollback Rate",
            DisplayRateTimeScale = TimeSpan.FromSeconds(1),
        };
        _totalDeadlocksCounter ??= new PollingCounter("total-deadlocks", this, () => Volatile.Read(ref _totalDeadlocks))
        {
            DisplayName = "Total Deadlocks",
        };
        _lockWaitRateCounter ??= new IncrementingPollingCounter("lock-waits-per-second", this, () => Volatile.Read(ref _totalLockWaits))
        {
            DisplayName = "Lock Wait Rate",
            DisplayRateTimeScale = TimeSpan.FromSeconds(1),
        };
    }

    private static string TypeName(Exception exception) => exception.GetType().FullName ?? exception.GetType().Name;

    /// <summary>
    /// Names a lock resource for a payload by its kind and object only. An entry's id is never
    /// written: a unique-index or key-value key lock's entry is the key's unseeded 64-bit hash, and
    /// a hash of a small-domain key is the key (plan D8). The lock manager cannot tell such an entry
    /// from a Sql row-location entry, which share <see cref="LockResourceKind.Entry"/> and the
    /// pre-acquire identity the Sql executor depends on, so every entry is written as its object;
    /// the object id still locates the table, collection or key space.
    /// </summary>
    /// <param name="resource">The resource.</param>
    /// <returns><c>Database</c>, <c>Object:&lt;objectId&gt;</c> or <c>Entry:&lt;objectId&gt;</c>.</returns>
    internal static string DescribeResource(LockResource resource) => resource.Kind switch
    {
        LockResourceKind.Database => nameof(LockResourceKind.Database),
        LockResourceKind.Object => string.Concat(nameof(LockResourceKind.Object), ":", resource.ObjectId.ToString(CultureInfo.InvariantCulture)),
        _ => string.Concat(nameof(LockResourceKind.Entry), ":", resource.ObjectId.ToString(CultureInfo.InvariantCulture)),
    };
}
