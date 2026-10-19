using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Storage;

// Deviates from namespace-matches-assembly: this model-local helper is compiled
// from the Database root's shared source into each model assembly.
namespace Assimalign.Cohesion.Database;

/// <summary>
/// The engine-owned checkpointer every engine model runs: one copy of the pass the five engines
/// share (database-area.md rule 8), compiled into each model, because the checkpointer is each
/// engine's internal worker rather than root API. Its name (<c>{engine}/checkpoint</c>), kind and
/// cadence are fixed by its constructor, as every <see cref="DatabaseEngineWorker"/>'s are since
/// phase 3 of the concrete-types plan (#1259). A model supplies its databases and what a checkpoint
/// of one is.
/// </summary>
/// <typeparam name="TDatabase">The model's database type.</typeparam>
/// <remarks>
/// <para>
/// A database is checkpointed when its journal reaches the engine's checkpoint size, or when the
/// checkpoint interval has passed since its last checkpoint and its journal received records since
/// (#1254). A storage whose journal reaches the size wakes the worker at once through the engine's
/// checkpoint signal, as PostgreSQL's <c>XLogWrite</c> requests a checkpoint once
/// <c>max_wal_size</c> is consumed (<c>src/backend/access/transam/xlog.c:2579-2584</c>); otherwise
/// the worker polls at most once a second for the time backstop, PostgreSQL's
/// <c>checkpoint_timeout</c> (<c>src/backend/postmaster/checkpointer.c:405-412</c>).
/// </para>
/// <para>
/// <b>Each database's checkpoint runs on a lane of its own.</b> The pass hands a due database's
/// checkpoint to <see cref="DatabaseCheckpointLanes"/> and waits for it while no other database
/// needs the worker, so a checkpoint that ends is settled in the pass that started it. A checkpoint
/// that hangs or crawls in its device is left to run alone once another database's journal reaches
/// its size, or the poll interval passes: the passes that follow leave its database out until it
/// ends, and every other database keeps being checkpointed meanwhile. Before the lanes, the worker
/// visited the databases one by one on its own thread, so one database's device held every other
/// database's journal truncation for as long as it took to answer, or to fail.
/// </para>
/// <para>
/// The checkpoint runs through the database's transaction coordinator, under its statement apply
/// gate, so a sustained statement load cannot keep it out; it never waits for the gate: when a
/// statement holds it, the coordinator defers the checkpoint to that statement's end
/// (<c>TransactionCoordinator.TryCheckpoint</c>), and the database is retried by a later pass. A
/// storage still busy (<see cref="StorageTransactionException"/>) is retried at the next poll. An
/// offline database (#1243) is skipped: nothing may be written to it until it is reopened.
/// </para>
/// <para>
/// <b>A failed checkpoint is one database's failure (#1268).</b> Any other failure — a page write
/// the checkpoint's flush could not make, a deferred checkpoint's failure the coordinator hands
/// back — is reported for that database, by the pass that settles the checkpoint. Later passes skip
/// the failing database for <see cref="DatabaseEngineWorker.FailureBackoff"/> and then retry it,
/// since its journal is still due. The failure stays recorded until a pass settles a checkpoint
/// that finished; a checkpoint deferred to a running statement, or still running on its lane, keeps
/// it recorded, and records nothing for a database without one. A failure that took the database
/// offline (a failed drain of the journal's append buffer or durable flush, or a header slot write
/// that failed) is not the worker's: the database is skipped from then on and the engine lists it
/// offline.
/// </para>
/// <para>
/// <b>A database whose checkpoints keep failing goes offline</b> (owner decision 25 of
/// 2026-10-06), on whichever comes first. Once its checkpoints have kept failing for the engine's
/// worker failure window across its minimum of failed passes (owner decision 42 of 2026-10-07),
/// the worker base takes it offline with <see cref="StorageOfflineCause.CheckpointFailures"/>. A
/// checkpoint that fails for the second pass or more in a row while one of the database's journals
/// has reached the engine's journal size limit takes it offline with
/// <see cref="StorageOfflineCause.JournalSizeLimit"/> (owner decision 41): the journal is no longer
/// truncated, and under load it would fill the device long before the window passes. One failure
/// is not enough: a journal can reach the cap with no failure at all (a
/// checkpoint deferred to a long statement, a busy storage, a write burst, #1283), and a single
/// transient failure of such a database is retried like any other; the second failure in a row,
/// a backoff later, is what says its checkpoints keep failing. PostgreSQL has no such cap: its
/// <c>max_wal_size</c> is a soft limit the WAL may pass under heavy load
/// (<c>doc/src/sgml/config.sgml:4010-4014</c>), and while its checkpoints fail the WAL grows until a
/// WAL write fails on a full device and the server stops (<c>XLogWrite</c>,
/// <c>src/backend/access/transam/xlog.c:2529-2532</c>). Only a
/// failure counts toward either: a checkpoint deferred to a statement or refused by a busy storage
/// (#1283) never takes a database offline by itself, however long its journal grows.
/// </para>
/// </remarks>
internal abstract class DatabaseCheckpointWorker<TDatabase> : DatabaseEngineWorker
    where TDatabase : class
{
    /// <summary>
    /// The longest the worker sleeps between two looks at the time backstop and at a busy storage,
    /// and the longest it waits on one database's checkpoint while nothing else signals it.
    /// </summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How many checkpoints in a row must fail, with the journal at the engine's journal size
    /// limit, before the database goes offline for it: two, so a journal that reached the cap with
    /// no failure (#1283) is not taken offline by one transient failure (the class remarks).
    /// </summary>
    internal const int JournalSizeLimitFailures = 2;

    private readonly DatabaseCheckpointLanes _lanes;
    private readonly long _journalSizeLimit;

    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseCheckpointWorker{TDatabase}"/> class,
    /// named <c>{engineName}/checkpoint</c>, of kind <see cref="DatabaseEngineWorkerKind.Checkpoint"/>.
    /// </summary>
    /// <param name="engineName">The name of the engine whose databases are checkpointed.</param>
    /// <param name="interval">The engine's checkpoint interval: the time backstop between checkpoints.</param>
    /// <param name="journalSizeLimit">
    /// The engine's journal size limit, in bytes, as <see cref="DatabaseWorkerLimits.GetJournalSizeLimit"/>
    /// resolved it: the second failed checkpoint in a row of a database one of whose journals holds
    /// this much takes the database offline.
    /// </param>
    protected DatabaseCheckpointWorker(string engineName, TimeSpan interval, long journalSizeLimit)
        : base(engineName + "/checkpoint", DatabaseEngineWorkerKind.Checkpoint, interval)
    {
        _lanes = new DatabaseCheckpointLanes(engineName + "/" + DatabaseEngineWorkerKind.Checkpoint + " lane");
        _journalSizeLimit = journalSizeLimit;
    }

    /// <summary>
    /// Gets the engine's checkpoint signal: set when a journal reaches the checkpoint size.
    /// </summary>
    protected abstract ManualResetEventSlim CheckpointNeededSignal { get; }

    /// <summary>
    /// Stops the worker's lanes once the engine stopped its pump: waits for every checkpoint still
    /// running, so none outlives the engine's storages. The worker base's release hook, which the
    /// owning engine runs once (concrete-types plan, row 7); it was the worker's
    /// <see cref="IDisposable.Dispose"/> until the engines' disposable type tests went.
    /// </summary>
    /// <returns>A completed task, once every lane ended.</returns>
    protected override ValueTask DisposeAsyncCore()
    {
        _lanes.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>Gets the engine's open databases.</summary>
    /// <returns>The snapshot of the engine's open databases.</returns>
    protected abstract TDatabase[] GetDatabases();

    /// <summary>Gets a database's name, unique among the engine's open databases.</summary>
    /// <param name="database">The database.</param>
    /// <returns>The name.</returns>
    protected abstract string GetName(TDatabase database);

    /// <summary>Gets whether a database is offline (#1243).</summary>
    /// <param name="database">The database.</param>
    /// <returns>True when it is offline.</returns>
    protected abstract bool IsOffline(TDatabase database);

    /// <summary>Gets whether a database is still open in the engine.</summary>
    /// <param name="database">The database.</param>
    /// <returns>True while the engine holds it open.</returns>
    protected abstract bool IsOpen(TDatabase database);

    /// <summary>
    /// Gets the length, in bytes, of a database's longest journal: what the engine's journal size
    /// limit is compared with when a checkpoint of the database fails.
    /// </summary>
    /// <param name="database">The database.</param>
    /// <returns>The longest of its storages' <c>JournalLength</c>.</returns>
    protected abstract long GetJournalLength(TDatabase database);

    /// <summary>Gets whether one of a database's storages is due for a checkpoint.</summary>
    /// <param name="database">The database.</param>
    /// <param name="interval">The time backstop.</param>
    /// <returns>True when a checkpoint is due.</returns>
    protected abstract bool IsCheckpointDue(TDatabase database, TimeSpan interval);

    /// <summary>
    /// Checkpoints a database's due storages, on a lane thread.
    /// </summary>
    /// <param name="database">The database.</param>
    /// <param name="interval">The time backstop.</param>
    /// <param name="cancellationToken">The pass's cancellation.</param>
    /// <returns>True when the checkpoint finished; false when a statement holding the apply gate took it over.</returns>
    protected abstract bool Checkpoint(TDatabase database, TimeSpan interval, CancellationToken cancellationToken);

    /// <inheritdoc />
    protected sealed override void WaitForTrigger(CancellationToken cancellationToken)
    {
        try
        {
            CheckpointNeededSignal.Wait(PollWait, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The pump is stopping; Run observes the token and exits.
        }
        catch (ObjectDisposedException)
        {
            // The engine is closing.
        }
    }

    /// <inheritdoc />
    protected sealed override void RunIterationCore(CancellationToken cancellationToken)
    {
        // Reset before the pass: a journal that reaches the size mid-pass sets it again, and so
        // does a checkpoint the pass leaves running when it ends.
        CheckpointNeededSignal.Reset();
        var interval = Interval;
        var wait = PollWait;

        _lanes.BeginPass();
        try
        {
            foreach (var database in GetDatabases())
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                Visit(database, interval, wait, cancellationToken);
            }
        }
        finally
        {
            _lanes.EndPass();
        }
    }

    private TimeSpan PollWait => Interval < PollInterval ? Interval : PollInterval;

    private void Visit(TDatabase database, TimeSpan interval, TimeSpan wait, CancellationToken cancellationToken)
    {
        string name = GetName(database);
        var state = _lanes.Collect(database, out var ended);
        if (state == DatabaseCheckpointLanes.LaneState.Running)
        {
            // Its checkpoint still runs on its lane: the database waits for it, and a failure
            // recorded for it stays until a checkpoint finishes. An offline one is not begun.
            if (!IsOffline(database))
            {
                ReportUnfinished(name);
            }

            return;
        }

        // An offline database is not begun: the engine reports it (#1243), and a failure the worker
        // recorded for it ends. A database whose failure is backing off waits its turn.
        if (IsOffline(database))
        {
            return;
        }

        bool begun = BeginDatabase(name);
        if (state == DatabaseCheckpointLanes.LaneState.Ended)
        {
            // A checkpoint an earlier pass left running ended: settled as that pass would have.
            // Only a finished one lets this pass check the database again, so a journal that
            // reached its size meanwhile is not left to the poll.
            Settle(database, name, ended, cancellationToken);
            if (!ended.Finished || ended.Failure is not null)
            {
                return;
            }
        }

        if (!begun)
        {
            return;
        }

        DatabaseCheckpointLanes.Outcome outcome;
        try
        {
            if (!IsCheckpointDue(database, interval))
            {
                return;
            }

            // The worker waits for the checkpoint only while no other database needs it.
            if (!_lanes.Run(database, () => Checkpoint(database, interval, cancellationToken), CheckpointNeededSignal, wait, cancellationToken, out outcome))
            {
                // Left running on its lane; a later pass collects it.
                ReportUnfinished(name);
                return;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            outcome = new DatabaseCheckpointLanes.Outcome(false, exception);
        }

        Settle(database, name, outcome, cancellationToken);
    }

    private void Settle(TDatabase database, string name, DatabaseCheckpointLanes.Outcome outcome, CancellationToken cancellationToken)
    {
        switch (outcome.Failure)
        {
            case null when outcome.Finished:
                return;

            case null:
                // Deferred to the statement holding the apply gate, which runs it as it ends.
                ReportUnfinished(name);
                return;

            case StorageTransactionException:
                // A transaction is active on this storage; retry on the next pass.
                ReportUnfinished(name);
                return;

            case ObjectDisposedException when !IsOpen(database):
                // The snapshot can race a database drop; nothing left to checkpoint.
                return;

            case OperationCanceledException when cancellationToken.IsCancellationRequested:
                ExceptionDispatchInfo.Throw(outcome.Failure);
                return;

            default:
                // A failure that took the database offline (#1243, #1252, #1268) is reported through
                // the engine's offline list, and the next pass skips the database; any other is
                // this pass's failure, and a later pass retries the checkpoint. Reporting it takes
                // the database offline once its checkpoints kept failing for the engine's window
                // across its minimum of passes (owner decision 42); a second failure in a row with
                // the journal past the engine's cap takes it offline sooner (owner decisions 25
                // and 41).
                if (!IsOffline(database))
                {
                    int failures = ReportFailure(name, outcome.Failure);
                    if (failures >= JournalSizeLimitFailures)
                    {
                        GiveUpOnJournal(database, name, failures, outcome.Failure);
                    }
                }

                return;
        }
    }

    /// <summary>
    /// Takes a database whose checkpoints just failed for the second pass or more in a row offline
    /// when one of its journals has reached the engine's journal size limit (owner decision 25): its
    /// checkpoints keep failing, so nothing truncates the journal.
    /// </summary>
    private void GiveUpOnJournal(TDatabase database, string name, int failures, Exception failure)
    {
        if (IsOffline(database))
        {
            return;
        }

        long length = GetJournalLength(database);
        if (length >= _journalSizeLimit)
        {
            TakeDatabaseOffline(
                name,
                StorageOfflineCause.JournalSizeLimit,
                $"its journal holds {length} bytes, past the engine's limit of {_journalSizeLimit} bytes, while the engine's checkpoint " +
                $"worker '{Name}' failed on database '{name}' on {failures} passes in a row",
                failure);
        }
    }
}
