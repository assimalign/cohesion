using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Internal;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// The base of every database engine: a data machine that manages logical databases, owns its
/// background workers and the servers composed beneath it, and is disposed once.
/// </summary>
/// <remarks>
/// <para>
/// <b>An engine is a data machine, not a service.</b> It is operational from creation: a leaf
/// attaches its built-in workers from its constructor (<see cref="AttachWorker"/>), and each one
/// starts pumping on a dedicated background thread the engine owns. Disposal is the one lifecycle
/// transition, in a fixed order the base owns: the servers composed beneath the engine are disposed
/// (last attached first), the worker pumps are stopped and joined, the workers are disposed (last
/// attached first), and then the leaf closes its databases (<see cref="DisposeAsyncCore"/>). Every
/// step runs whatever an earlier one threw, and the failures are reported together. Disposal is
/// idempotent.
/// </para>
/// <para>
/// <b>Composition is attached, then frozen.</b> A leaf's internal build path attaches the workers
/// and servers composed for it (<see cref="AttachWorker"/>, <see cref="AttachServer"/>) and then
/// calls <see cref="CompleteComposition"/>; an attach after that, or after disposal, throws. The
/// products belong to the engine from the moment they are attached. A server must front this
/// engine, and no product is attached twice.
/// </para>
/// <para>
/// <b>A database closed outside the engine is forgotten</b> (owner decision 33 of 2026-10-06,
/// #1289). A holder may dispose a database directly, or through a session's database; when that
/// close ends, the database tells its engine, and the leaf stops tracking it
/// (<see cref="ForgetClosedDatabaseCore"/>), so a later <see cref="OpenDatabaseAsync"/> opens it
/// again from its files. Until the close ends the leaf still tracks the closing database, so its
/// workers keep skipping a closed one; <see cref="OpenDatabaseAsync"/> waits for such a close to
/// end before it opens the database again, <see cref="TryGetDatabase"/> does not report it, and
/// the leaf's own disposal of a database (a drop, a reopen after going offline, the engine's
/// disposal) waits for a close a holder started, so nothing reuses the database's files while
/// its close still runs.
/// </para>
/// <para>
/// <b>State.</b> <see cref="State"/> is folded from the engine's own life and its workers (#1268):
/// <see cref="EngineState.Disposed"/> once disposal started, <see cref="EngineState.Faulted"/>
/// while a worker holds a failure it has not worked off (<see cref="DatabaseEngineWorker.Fault"/>)
/// or a worker's loop escaped, and <see cref="EngineState.Running"/> otherwise. An offline database
/// is not a worker failure; <see cref="OfflineDatabases"/> lists it. A worker failure is either one
/// database's or the engine's as a whole, and the engine tells the two apart (owner decision 42 of
/// 2026-10-07): <see cref="HasFailingWorker"/> says whether a worker whose failures can take a
/// database offline holds a failure of a named database, and <see cref="HasEngineWideFailure"/>
/// whether one holds a failure no database owns, so a server can refuse only the database whose
/// work fails.
/// </para>
/// <para>
/// <b>Persistent worker failures take a database offline</b> (owner decisions 25 of 2026-10-06 and
/// 42 of 2026-10-07). When a checkpoint, page write-back, write-ahead flush or version-purge worker
/// keeps failing on one database for at least <see cref="WorkerFailureWindow"/> and across at least
/// <see cref="WorkerFailureMinimumPasses"/> failed passes in a row, or the database's journal passes
/// the engine's cap while its checkpoints keep failing, the engine gives up on that database: the
/// leaf takes it offline (<see cref="TakeDatabaseOfflineCore"/>) through the same machinery a failed
/// durable flush uses (#1243), with a storage cause that names the worker or the cap, and every
/// later operation on it is refused with the model's offline code until it is reopened. Neo4j
/// panics a database the same way once its checkpoint fails ten times in a row
/// (<c>community/kernel/src/main/java/org/neo4j/wal/checkpoint/CheckPointScheduler.java:41-75</c>,
/// <c>community/monitoring/src/main/java/org/neo4j/monitoring/DatabaseHealth.java:74-85</c>); until
/// then each failure is retried a second later, as PostgreSQL's background workers do
/// (<c>src/backend/postmaster/checkpointer.c:286-345</c>). Only that database goes offline: the
/// workers keep serving the engine's other databases, and a database already offline or closed is
/// never counted.
/// </para>
/// <para>
/// <b>The give-up never runs on a worker's thread.</b> Taking a database offline latches its
/// journal under the journal's lock, which a durable flush holds for as long as its fsync takes. A
/// worker that took a database offline on its own thread would therefore wait out a hung fsync of
/// that database, and every other database the worker serves would wait with it, the very stall
/// the checkpoint lanes isolate (#1268). The engine queues the give-up to the thread pool instead,
/// one at a time per database, and the worker's pass goes on at once. Once the database is offline
/// the engine forgets every worker's failure record of it, as it does whenever a database of the
/// engine closes, so the engine reports <see cref="EngineState.Running"/> again at once, and a
/// reopened database counts its failures from one. The engine's disposal waits for a give-up still
/// running before it closes its databases.
/// </para>
/// <para>
/// <b>Shape (concrete-types plan, phase 3, #1259).</b> Every public member is non-virtual and owns
/// the argument, disposed and cancellation checks before it calls a protected core; the one
/// abstract public member is <see cref="OfflineDatabases"/>, state the leaf computes. The leaves
/// live in the model assemblies, so the constructor is <c>protected</c>. A leaf re-exposes its
/// typed database with <c>new</c> members that await the public members here, never the cores.
/// The hosting layer composes every model through this base (concrete-types plan, phase 6, #1262).
/// </para>
/// </remarks>
// Deviates from the repo interface-first rule per design decision: Database engines are concrete-first — abstract bases with protected cores and sealed model leaves (owner, 2026-10-04; database-area.md).
public abstract class DatabaseEngine : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// The fewest failed passes in a row of one database that the engine gives up on when its
    /// options state none: three (owner decision 42 of 2026-10-07). See
    /// <see cref="WorkerFailureMinimumPasses"/>.
    /// </summary>
    /// <remarks>
    /// The window does the work at every worker's pace (<see cref="DefaultWorkerFailureWindow"/>);
    /// the minimum is there for the slow ones. A worker that visits a failing database seldom, or
    /// whose one attempt took long, can see the window pass after one or two failures: a deferred
    /// undo on its doubling backoff, a checkpoint that hung on its lane for longer than the window
    /// before it failed. Three failed passes in a row mean the failure was retried,
    /// twice, before the database goes offline. The journal cap (owner decision 41) needs two
    /// failed checkpoints in a row and is not held back by this minimum.
    /// </remarks>
    public const int DefaultWorkerFailureMinimumPasses = 3;

    // The operation names DatabaseOperationFailed writes (event-sources plan, event 13).
    private const string createOperation = "Create";
    private const string openOperation = "Open";
    private const string dropOperation = "Drop";

    private readonly string _name;
    private readonly EngineModel _model;
    private readonly TimeSpan _workerFailureWindow;
    private readonly int _workerFailureMinimumPasses;
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();
    private readonly List<DatabaseEngineWorker> _workers = [];
    private readonly List<DatabaseServer> _servers = [];
    private readonly List<Thread> _threads = [];
    private readonly CancellationTokenSource _stop = new();

    // The give-ups queued to the thread pool, one per database at most, each until it ended
    // (owner decision 25): the disposal waits for them before the leaf closes its databases.
    private readonly ConcurrentDictionary<string, Task> _givingUp = new(StringComparer.OrdinalIgnoreCase);

    // The inventories as published to readers: replaced whole on every attach, so a reader never
    // enumerates a list being changed.
    private IReadOnlyList<DatabaseEngineWorker> _workerView = [];
    private IReadOnlyList<DatabaseServer> _serverView = [];

    // The last exception that escaped a worker's Run: its pump ran it again after the backoff, and
    // the engine reports Faulted until it is disposed, because it cannot tell when such a worker
    // is healthy again. DatabaseEngineWorker's loop lets nothing escape, so this stays null for
    // every worker the base can attach; the frame is kept so the pump never ends early.
    private Exception? _workerRunFault;
    private bool _composed;
    private int _disposed;

    /// <summary>
    /// Initializes a new engine with its name and its data model, and the default worker failure
    /// window and minimum (<see cref="DefaultWorkerFailureWindow"/>,
    /// <see cref="DefaultWorkerFailureMinimumPasses"/>), measured by the system clock.
    /// </summary>
    /// <param name="name">The logical name of the engine instance.</param>
    /// <param name="model">The data model the engine implements.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null, empty or white space.</exception>
    protected DatabaseEngine(string name, EngineModel model)
        : this(name, model, DefaultWorkerFailureWindow, DefaultWorkerFailureMinimumPasses)
    {
    }

    /// <summary>
    /// Initializes a new engine with its name, its data model, and when it gives up on a database
    /// whose worker failures persist (owner decision 42 of 2026-10-07).
    /// </summary>
    /// <param name="name">The logical name of the engine instance.</param>
    /// <param name="model">The data model the engine implements.</param>
    /// <param name="workerFailureWindow">
    /// How long a worker's failures of one database must persist before the engine takes the
    /// database offline (<see cref="WorkerFailureWindow"/>): positive, and at most
    /// <see cref="MaximumWorkerFailureWindow"/>.
    /// </param>
    /// <param name="workerFailureMinimumPasses">
    /// How many failed passes in a row those failures must span at least
    /// (<see cref="WorkerFailureMinimumPasses"/>): at least one.
    /// </param>
    /// <param name="timeProvider">
    /// The clock that measures the window; <see cref="TimeProvider.System"/> when null. A model's
    /// tests pass a clock they move by hand, so a test crosses the window without waiting for it.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null, empty or white space.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="workerFailureWindow"/> is not positive or is longer than
    /// <see cref="MaximumWorkerFailureWindow"/>; or <paramref name="workerFailureMinimumPasses"/> is
    /// less than one.
    /// </exception>
    protected DatabaseEngine(string name, EngineModel model, TimeSpan workerFailureWindow, int workerFailureMinimumPasses, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(workerFailureWindow, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(workerFailureWindow, MaximumWorkerFailureWindow);
        ArgumentOutOfRangeException.ThrowIfLessThan(workerFailureMinimumPasses, 1);
        _name = name;
        _model = model;
        _workerFailureWindow = workerFailureWindow;
        _workerFailureMinimumPasses = workerFailureMinimumPasses;
        _timeProvider = timeProvider ?? TimeProvider.System;
        DatabaseEventSource.Log.EngineCreated(this);
    }

    /// <summary>
    /// Gets how long a database's worker failures persist before an engine whose options state no
    /// window gives up on it: one hundred seconds, the window Neo4j tolerates checkpoint failures
    /// for (owner decisions 35 and 42 of 2026-10-07). See <see cref="WorkerFailureWindow"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Neo4j panics a database after ten consecutive checkpoint failures
    /// (<c>MAX_CONSECUTIVE_FAILURES_TOLERANCE</c>,
    /// <c>community/kernel/src/main/java/org/neo4j/wal/checkpoint/CheckPointScheduler.java:41-42</c>),
    /// and it checks for a checkpoint every ten seconds by default
    /// (<c>DEFAULT_CHECKING_FREQUENCY_MILLIS</c>,
    /// <c>community/kernel/src/main/java/org/neo4j/wal/checkpoint/CheckPointThreshold.java:40</c>),
    /// so its ten failures span about a hundred seconds. A device that stops answering for less
    /// than that (a storage path failover, say) therefore leaves the databases it holds online,
    /// and a hosted application reopens one that went offline once the device answers again (owner
    /// decision 22). An engine that must ride out longer outages widens its window; one that must
    /// give up sooner narrows it.
    /// </para>
    /// <para>
    /// The window is time, not a count of passes, so every worker keeps it whatever its pace.
    /// Owner decision 35 kept Neo4j's window as a hundred failed passes, which is a hundred seconds
    /// only for a worker that visits a failing database once a second; a version purge's full
    /// pass, once per maintenance interval, took about a hundred minutes to give up, and a
    /// rolled-back writer's deferred undo, retried at 100 ms doubling up to that interval, about an
    /// hour and a half while the writer kept its locks. Decision 42 measures the window instead: a
    /// database goes offline at the first failed pass at least this long after its first, once
    /// <see cref="DefaultWorkerFailureMinimumPasses"/> passes in a row failed. At the defaults
    /// that is about a hundred seconds for the checkpoint, page write-back and write-ahead flush
    /// workers, about 102 s for a deferred undo (its tenth retry) and about 101 s for a failing
    /// version purge's full pass, which the model engines retry a backoff after each failure (owner
    /// decision 46; two minutes, its third pass, while they retried it only at the next interval).
    /// </para>
    /// </remarks>
    public static TimeSpan DefaultWorkerFailureWindow { get; } = TimeSpan.FromSeconds(100);

    /// <summary>
    /// Gets the longest <see cref="WorkerFailureWindow"/> an engine accepts:
    /// <see cref="int.MaxValue"/> milliseconds (about 24.8 days), the bound the area puts on its
    /// longest timers (<see cref="Storage.Storage.MaximumGroupCommitWindow"/>). An engine that
    /// should never give up on a failing database by time alone sets this window, or a minimum of
    /// passes no worker reaches.
    /// </summary>
    public static TimeSpan MaximumWorkerFailureWindow { get; } = TimeSpan.FromMilliseconds(int.MaxValue);

    /// <summary>
    /// Gets the logical name of this engine instance.
    /// </summary>
    public string Name => _name;

    /// <summary>
    /// Gets the data model this engine implements.
    /// </summary>
    public EngineModel Model => _model;

    /// <summary>
    /// Gets how long a checkpoint, page write-back, write-ahead flush or version-purge worker's
    /// failures of one database must persist before the engine takes that database offline (owner
    /// decisions 25 of 2026-10-06 and 42 of 2026-10-07), as the constructor set it from the
    /// engine's options.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The window is measured from the first failed pass of the database's current streak, on the
    /// engine's clock; the engine gives up at the first failed pass at least this long after it,
    /// once the streak also spans <see cref="WorkerFailureMinimumPasses"/> failed passes in a row.
    /// A streak lasts from a worker's first failure on the database to the pass that finishes the
    /// database's work again; a pass that skips the database while its failure backs off counts
    /// neither way, and keeps the streak's start.
    /// </para>
    /// <para>
    /// The clock keeps running while the database is only busy: a pass that leaves its work
    /// unfinished without a failure (a storage busy with a transaction, a checkpoint deferred to a
    /// running statement or still running on its lane) neither ends the streak nor stops its clock,
    /// so three failed passes with a long busy stretch between them reach the window (owner decision
    /// 42 review).
    /// </para>
    /// <para>
    /// A failure on a database already offline, or one its holder closed, is not counted, and the
    /// streak ends when the engine takes the database offline or the database closes, so a
    /// reopened database starts a new one.
    /// </para>
    /// </remarks>
    public TimeSpan WorkerFailureWindow => _workerFailureWindow;

    /// <summary>
    /// Gets how many failed passes in a row a worker's failures of one database must span, besides
    /// lasting <see cref="WorkerFailureWindow"/>, before the engine takes that database offline
    /// (owner decision 42 of 2026-10-07), as the constructor set it from the engine's options.
    /// </summary>
    /// <remarks>
    /// Several failures of one database that one pass reports (one per file set) count once, as a
    /// failed pass.
    /// </remarks>
    public int WorkerFailureMinimumPasses => _workerFailureMinimumPasses;

    /// <summary>
    /// Gets the clock that measures <see cref="WorkerFailureWindow"/>, as the constructor set it.
    /// </summary>
    internal TimeProvider TimeProvider => _timeProvider;

    /// <summary>
    /// Gets the observational state of the engine: <see cref="EngineState.Running"/> from creation,
    /// <see cref="EngineState.Faulted"/> while a background worker keeps failing (the engine keeps
    /// serving), and <see cref="EngineState.Disposed"/> once disposal started.
    /// </summary>
    /// <remarks>
    /// <see cref="EngineState.Faulted"/> folds two kinds of failure: one database's
    /// (<see cref="HasFailingWorker"/>) and the engine's as a whole
    /// (<see cref="HasEngineWideFailure"/>). A component that refuses work for a failing worker
    /// reads those, not the state, so a failure of one database refuses no other (owner decision 42
    /// of 2026-10-07).
    /// </remarks>
    public EngineState State
    {
        get
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return EngineState.Disposed;
            }

            if (Volatile.Read(ref _workerRunFault) is not null)
            {
                return EngineState.Faulted;
            }

            var workers = Volatile.Read(ref _workerView);
            for (int index = 0; index < workers.Count; index++)
            {
                if (workers[index].Fault is not null)
                {
                    return EngineState.Faulted;
                }
            }

            return EngineState.Running;
        }
    }

    /// <summary>
    /// Gets whether the engine has failed as a whole: a worker holds a failure no database owns (a
    /// pass that failed before it settled its databases, or a trigger wait that failed), or a
    /// worker's loop escaped. A point-in-time read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// While it is true the engine is <see cref="EngineState.Faulted"/>, and nothing tells which
    /// databases the failing work concerns, so a server that refuses work for a failing worker
    /// refuses it for every database (owner decision 42 of 2026-10-07): Blob's server refuses its
    /// start, new connections, handshakes and exchanges. It turns false once the failing worker's
    /// next pass runs to its end; an escaped loop keeps it true until the engine is disposed. It
    /// does not read disposal: <see cref="State"/> says that.
    /// </para>
    /// <para>
    /// A failure of one database is not the engine's as a whole: <see cref="HasFailingWorker"/>
    /// reports it for that database alone. Nor is a give-up of one database that the leaf could not
    /// complete (<see cref="TakeDatabaseOfflineCore"/> threw): the worker holds it in its
    /// <see cref="DatabaseEngineWorker.Fault"/>, so the engine stays
    /// <see cref="EngineState.Faulted"/>, but it concerns that database, whose record still names it
    /// (owner decision 42 review).
    /// </para>
    /// </remarks>
    public bool HasEngineWideFailure
    {
        get
        {
            if (Volatile.Read(ref _workerRunFault) is not null)
            {
                return true;
            }

            var workers = Volatile.Read(ref _workerView);
            for (int index = 0; index < workers.Count; index++)
            {
                if (workers[index].HoldsOwnFailure)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Gets whether a background worker of the engine whose failures can take a database offline
    /// (a checkpoint, page write-back, write-ahead flush or version-purge worker) holds a failure of
    /// the named database that it has not worked off yet: its work on the database failed, and no
    /// pass has finished that work since (owner decision 42 of 2026-10-07). A point-in-time read
    /// over the workers' failure records, the per-database part of <see cref="EngineState.Faulted"/>
    /// that can end with the database offline.
    /// </summary>
    /// <param name="name">The name of the database.</param>
    /// <returns>
    /// True while such a worker holds a failure of the database; false once a pass finished the
    /// database's work again, once the engine gave up on the database and took it offline, or the
    /// database closed (the engine then ends every worker's record of it), and for a database no
    /// such worker failed on. A database that went offline for a failure of its own storage (a
    /// failed durable flush, journal write or header write) keeps a worker's record until that
    /// worker's next pass, which skips the offline database and so ends it.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    /// <remarks>
    /// <para>
    /// A server that refuses work for a failing worker reads this for the database a session or
    /// exchange targets, so it refuses that database alone while the engine's other databases are
    /// served: Blob's server does, with its model's code, until the failure ends or the engine
    /// gives up on the database, which is then refused as offline. The server checks that the
    /// database is online first, so an offline database is refused as offline even while a
    /// worker's record of it lingers. A failure of the engine as a whole is not a database's:
    /// <see cref="HasEngineWideFailure"/> reports it.
    /// </para>
    /// <para>
    /// An index-maintenance worker's failures do not count (owner decision 42 review): they cost
    /// space, not durability, and never take a database offline, so a refusal they caused would
    /// last for as long as the work kept failing. They still make the engine
    /// <see cref="EngineState.Faulted"/> and show in the worker's
    /// <see cref="DatabaseEngineWorker.Fault"/>.
    /// </para>
    /// <para>
    /// It does not check disposal, as <see cref="State"/> does not, so a server's gate can read it
    /// beside the state without racing the engine's disposal; the disposal closes the databases,
    /// which ends their records.
    /// </para>
    /// </remarks>
    public bool HasFailingWorker(DatabaseName name)
    {
        ThrowIfEmpty(name);
        var workers = Volatile.Read(ref _workerView);
        for (int index = 0; index < workers.Count; index++)
        {
            var worker = workers[index];
            if (worker.TakesDatabasesOffline && worker.HoldsFailure(name))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the engine-owned background workers, in the order they were attached: a point-in-time
    /// snapshot, for observability.
    /// </summary>
    public IReadOnlyList<DatabaseEngineWorker> Workers => Volatile.Read(ref _workerView);

    /// <summary>
    /// Gets the servers composed beneath this engine, in the order they were attached. The engine
    /// owns their disposal; an application snapshots the list and drives start and stop.
    /// </summary>
    public IReadOnlyList<DatabaseServer> Servers => Volatile.Read(ref _serverView);

    /// <summary>
    /// Gets the names of the open databases that are offline: a durable flush, a journal drain or a
    /// file header write of theirs failed, or the engine gave up on them after a worker's failures
    /// persisted (<see cref="WorkerFailureWindow"/>), and every operation on them is refused until
    /// <see cref="OpenDatabaseAsync"/> reopens them. Empty while every open database is online; a
    /// point-in-time snapshot. <see cref="GetOfflineError"/> says what took one offline.
    /// </summary>
    /// <remarks>
    /// State the leaf computes from its storages, so the one abstract public member (rule 4 of
    /// <c>database-area.md</c>). An offline database does not change <see cref="State"/>.
    /// </remarks>
    public abstract IReadOnlyList<DatabaseName> OfflineDatabases { get; }

    /// <summary>
    /// Gets whether disposal of the engine has started.
    /// </summary>
    protected bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>
    /// Creates a new logical database with the specified name.
    /// </summary>
    /// <param name="name">The name of the database to create.</param>
    /// <param name="cancellationToken">Observed before the database is created.</param>
    /// <returns>The newly created database.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    /// <exception cref="ObjectDisposedException">The engine has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the core ran.</exception>
    /// <exception cref="DatabaseException">A database with the same name already exists.</exception>
    public ValueTask<DatabaseInstance> CreateDatabaseAsync(DatabaseName name, CancellationToken cancellationToken = default)
    {
        ThrowIfEmpty(name);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        // The core is returned as it is while nobody listens; only an enabled source pays for the
        // wrapper that times it (event-sources plan, D5 a).
        if (!DatabaseEventSource.Log.IsEnabled(EventLevel.Error, EventKeywords.None))
        {
            return CreateDatabaseCoreAsync(name, cancellationToken);
        }

        return CreateDatabaseTracedAsync(name, cancellationToken);
    }

    /// <summary>
    /// Opens an existing logical database by name. A database that went offline, or that a holder
    /// closed outside the engine, is reopened: the returned instance is a new one, opened again
    /// from its files.
    /// </summary>
    /// <param name="name">The name of the database to open.</param>
    /// <param name="cancellationToken">
    /// Observed before the database is opened, and while the open waits for a holder's close of
    /// the database to end.
    /// </param>
    /// <returns>The opened database.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    /// <exception cref="ObjectDisposedException">The engine has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the core ran, or while the open waited for a close.</exception>
    /// <exception cref="DatabaseNotFoundException">The database does not exist.</exception>
    /// <remarks>
    /// A holder may close the database while the open runs. When the leaf's core hands back an
    /// instance whose close has started, the open waits for that close to end, after which the
    /// engine no longer tracks it (<see cref="ForgetClosedDatabaseCore"/>), and opens the
    /// database again (owner decision 33, #1289). The open never returns a closed instance.
    /// </remarks>
    public ValueTask<DatabaseInstance> OpenDatabaseAsync(DatabaseName name, CancellationToken cancellationToken = default)
    {
        ThrowIfEmpty(name);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (!DatabaseEventSource.Log.IsEnabled(EventLevel.Error, EventKeywords.None))
        {
            return OpenUnclosedAsync(name, waitedForClose: null, cancellationToken);
        }

        return OpenDatabaseTracedAsync(name, cancellationToken);
    }

    /// <summary>
    /// Drops an existing logical database and its storage.
    /// </summary>
    /// <param name="name">The name of the database to drop.</param>
    /// <param name="cancellationToken">Observed before the database is dropped.</param>
    /// <returns>A task that completes once the database is dropped.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    /// <exception cref="ObjectDisposedException">The engine has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the core ran.</exception>
    public ValueTask DropDatabaseAsync(DatabaseName name, CancellationToken cancellationToken = default)
    {
        ThrowIfEmpty(name);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (!DatabaseEventSource.Log.IsEnabled(EventLevel.Error, EventKeywords.None))
        {
            return DropDatabaseCoreAsync(name, cancellationToken);
        }

        return DropDatabaseTracedAsync(name, cancellationToken);
    }

    /// <summary>
    /// Enumerates the logical databases the engine manages. The engine state and the token are
    /// checked when the call is made, not when the enumeration starts.
    /// </summary>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    /// <returns>An async sequence of the databases.</returns>
    /// <exception cref="ObjectDisposedException">The engine has been disposed when the call is made.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled when the call is made.</exception>
    public IAsyncEnumerable<DatabaseInstance> GetDatabasesAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        return GetDatabasesCore(cancellationToken);
    }

    /// <summary>
    /// Attempts to retrieve an open database by name without throwing when it is not open.
    /// </summary>
    /// <param name="name">The name of the database.</param>
    /// <param name="database">When this method returns true, the database.</param>
    /// <returns>
    /// True when the database is open in the engine; otherwise false, for a database a holder is
    /// closing too (owner decision 33, #1289).
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    /// <exception cref="ObjectDisposedException">The engine has been disposed.</exception>
    public bool TryGetDatabase(DatabaseName name, [MaybeNullWhen(false)] out DatabaseInstance database)
    {
        ThrowIfEmpty(name);
        ThrowIfDisposed();
        if (TryGetDatabaseCore(name, out database) && !database.IsClosing)
        {
            return true;
        }

        database = null;
        return false;
    }

    /// <summary>
    /// Gets the storage error that took an open database offline, or null when the database is
    /// online, or not open in the engine: its <see cref="StorageOfflineException.Cause"/> says which
    /// device operation failed, or which background worker's work the engine gave up on, or that
    /// the journal passed the engine's cap. Every operation on the database is refused with the
    /// model's <see cref="DatabaseOfflineException"/>, whose inner exception this is.
    /// </summary>
    /// <param name="name">The name of the database.</param>
    /// <returns>The error, or null.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    /// <exception cref="ObjectDisposedException">The engine has been disposed.</exception>
    /// <remarks>
    /// The lookup companion of <see cref="OfflineDatabases"/>: a database it lists has an error
    /// here until it is reopened. A database a holder is closing is not open, so it has none.
    /// </remarks>
    public StorageOfflineException? GetOfflineError(DatabaseName name)
    {
        ThrowIfEmpty(name);
        ThrowIfDisposed();
        return GetOfflineErrorCore(name);
    }

    /// <summary>
    /// Disposes the engine: its servers, then its workers' pumps and the workers, then the leaf's
    /// databases. Idempotent; a call made while disposal runs returns at once.
    /// </summary>
    /// <exception cref="AggregateException">One or more components failed to close; every step ran.</exception>
    public void Dispose() => Task.Run(async () => await DisposeAsync().ConfigureAwait(false)).GetAwaiter().GetResult();

    /// <summary>
    /// Disposes the engine: its servers (last attached first), then its workers' pumps, then the
    /// workers (last attached first), then the leaf's databases (<see cref="DisposeAsyncCore"/>).
    /// Work committed before disposal is durable when the task completes. Idempotent; a call made
    /// while disposal runs returns at once.
    /// </summary>
    /// <returns>A task that completes once the engine is disposed.</returns>
    /// <exception cref="AggregateException">One or more components failed to close; every step ran.</exception>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        long disposeStarted = DatabaseEventSource.Log.EngineDisposeStart(this);
        DatabaseServer[] servers;
        DatabaseEngineWorker[] workers;
        Thread[] threads;
        lock (_sync)
        {
            servers = [.. _servers];
            workers = [.. _workers];
            threads = [.. _threads];
        }

        List<Exception>? failures = null;

        // Servers release their listeners and sessions before the workers or the storages go.
        for (int index = servers.Length - 1; index >= 0; index--)
        {
            try
            {
                await servers[index].DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                (failures ??= []).Add(failure);
            }
        }

        try
        {
            _stop.Cancel();
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            // A cancellation callback must not skip the joins.
            (failures ??= []).Add(failure);
        }

        foreach (var thread in threads)
        {
            try
            {
                thread.Join();
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                (failures ??= []).Add(failure);
            }
        }

        // A give-up a worker queued before its pump stopped ends before the databases close, so it
        // never takes a database offline while the leaf closes it. One queued after disposal
        // started finds the engine disposing and does nothing; a give-up never faults.
        foreach (var givingUp in _givingUp.Values)
        {
            await givingUp.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        // A worker may hold work of its own (a checkpoint left running on its lane); it ends
        // before the storages close. The engine owns every worker it attached, so it releases each
        // through the worker's internal entry point, which runs the worker's DisposeAsyncCore once
        // (concrete-types plan, row 7); nothing outside the engine can release an owned worker.
        for (int index = workers.Length - 1; index >= 0; index--)
        {
            try
            {
                await workers[index].ReleaseAsync().ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                (failures ??= []).Add(failure);
            }
        }

        try
        {
            await DisposeAsyncCore().ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            (failures ??= []).Add(failure);
        }

        _stop.Dispose();
        if (failures is not null)
        {
            DatabaseEventSource.Log.EngineDisposeFailed(this, failures.Count, failures[0]);
        }

        DatabaseEventSource.Log.EngineDisposeStop(this, failures?.Count ?? 0, disposeStarted);
        if (failures is not null)
        {
            throw new AggregateException($"One or more components of engine '{_name}' failed to close.", failures);
        }
    }

    /// <summary>
    /// Attaches a background worker to the engine and starts its pump on a dedicated background
    /// thread. The engine owns the worker from then on: it claims the worker before the pump
    /// starts, and stops the pump and releases the worker (its
    /// <see cref="DatabaseEngineWorker.DisposeAsyncCore"/> hook) when the engine is disposed.
    /// </summary>
    /// <param name="worker">The worker.</param>
    /// <exception cref="ArgumentNullException"><paramref name="worker"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">The engine has been disposed.</exception>
    /// <exception cref="InvalidOperationException">
    /// Composition was completed (<see cref="CompleteComposition"/>), the worker is already attached,
    /// another attached worker has the same name, or the worker is not free: another engine owns
    /// it, or it was released.
    /// </exception>
    protected void AttachWorker(DatabaseEngineWorker worker)
    {
        ArgumentNullException.ThrowIfNull(worker);
        lock (_sync)
        {
            ThrowIfDisposed();
            ThrowIfComposed();
            foreach (var existing in _workers)
            {
                if (ReferenceEquals(existing, worker))
                {
                    throw new InvalidOperationException("A composition product cannot be registered twice.");
                }

                if (string.Equals(existing.Name, worker.Name, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Worker name '{worker.Name}' is already registered.");
                }
            }

            // The claim comes before the pump starts, so no one can release the worker while its
            // pump runs, and a worker belongs to one engine: one another engine owns, or one already
            // released, is refused before it is pumped.
            if (!worker.TryClaim(this))
            {
                throw new InvalidOperationException(
                    $"Worker '{worker.Name}' belongs to one engine: another engine owns it, or it was released.");
            }

            var thread = new Thread(() => Pump(worker))
            {
                IsBackground = true,
                Name = worker.Name,
            };

            _workers.Add(worker);
            _threads.Add(thread);
            try
            {
                thread.Start();
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                _workers.Remove(worker);
                _threads.Remove(thread);

                // Not attached after all: free again, so the builder that refused it releases it.
                worker.Unclaim();
                throw;
            }

            Volatile.Write(ref _workerView, Array.AsReadOnly(_workers.ToArray()));
        }
    }

    /// <summary>
    /// Releases a worker no engine owns: a product a model's builder refused, or one a failed
    /// composition left unattached. It runs the worker's
    /// <see cref="DatabaseEngineWorker.DisposeAsyncCore"/> once, and does nothing on a worker an
    /// engine owns (that engine releases it after it stopped the worker's pump) or one already
    /// released.
    /// </summary>
    /// <param name="worker">The worker.</param>
    /// <returns>A task that completes once the worker's resources are released.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="worker"/> is null.</exception>
    /// <remarks>
    /// The worker has no public disposal (concrete-types plan, row 7): <see cref="Workers"/> is
    /// public, and a public release would let outside code release a worker whose pump still runs.
    /// A model's builder cannot reach the root's internals, so each model engine re-exposes this
    /// member internally and hands it to the builder's rollback.
    /// </remarks>
    protected static ValueTask ReleaseUnownedWorkerAsync(DatabaseEngineWorker worker)
    {
        ArgumentNullException.ThrowIfNull(worker);
        return worker.ReleaseUnownedAsync();
    }

    /// <summary>
    /// Attaches a server composed beneath the engine. The engine owns the server from then on and
    /// disposes it first when the engine is disposed; starting and stopping it is the application's.
    /// </summary>
    /// <param name="server">The server, which must front this engine.</param>
    /// <exception cref="ArgumentNullException"><paramref name="server"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">The engine has been disposed.</exception>
    /// <exception cref="InvalidOperationException">
    /// Composition was completed (<see cref="CompleteComposition"/>), the server fronts another
    /// engine, or it is already attached.
    /// </exception>
    protected void AttachServer(DatabaseServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        lock (_sync)
        {
            ThrowIfDisposed();
            ThrowIfComposed();
            if (!ReferenceEquals(server.Engine, this))
            {
                throw new InvalidOperationException("A nested server must front its owning engine.");
            }

            foreach (var existing in _servers)
            {
                if (ReferenceEquals(existing, server))
                {
                    throw new InvalidOperationException("A composition product cannot be registered twice.");
                }
            }

            _servers.Add(server);
            Volatile.Write(ref _serverView, Array.AsReadOnly(_servers.ToArray()));
        }
    }

    /// <summary>
    /// Freezes the engine's composition: every later <see cref="AttachWorker"/> or
    /// <see cref="AttachServer"/> throws <see cref="InvalidOperationException"/>. Idempotent.
    /// </summary>
    protected void CompleteComposition()
    {
        bool completed;
        int workerCount;
        int serverCount;
        lock (_sync)
        {
            completed = !_composed;
            _composed = true;
            workerCount = _workers.Count;
            serverCount = _servers.Count;
        }

        // The first call froze the composition: written once, outside the lock.
        if (completed)
        {
            DatabaseEventSource.Log.EngineComposed(this, workerCount, serverCount);
        }
    }

    /// <summary>
    /// Throws <see cref="ObjectDisposedException"/> once disposal of the engine has started.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The engine has been disposed.</exception>
    protected void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    /// <summary>
    /// Creates a database whose name and engine state <see cref="CreateDatabaseAsync"/> checked.
    /// </summary>
    /// <param name="name">The name of the database; never empty.</param>
    /// <param name="cancellationToken">Not canceled when the call starts.</param>
    /// <returns>The newly created database.</returns>
    protected abstract ValueTask<DatabaseInstance> CreateDatabaseCoreAsync(DatabaseName name, CancellationToken cancellationToken);

    /// <summary>
    /// Opens, or reopens when it is offline, a database whose name and engine state
    /// <see cref="OpenDatabaseAsync"/> checked.
    /// </summary>
    /// <param name="name">The name of the database; never empty.</param>
    /// <param name="cancellationToken">Not canceled when the call starts.</param>
    /// <returns>
    /// The opened database; or the tracked instance a holder is closing, which the leaf returns
    /// as it is, without waiting and without touching the database's files:
    /// <see cref="OpenDatabaseAsync"/> waits for that close to end and calls the core again.
    /// </returns>
    protected abstract ValueTask<DatabaseInstance> OpenDatabaseCoreAsync(DatabaseName name, CancellationToken cancellationToken);

    /// <summary>
    /// Forgets a database whose close has ended: the leaf stops tracking it if it still tracks
    /// that instance, so a later open opens the database again from its files (owner decision 33,
    /// #1289). Called once per database, by its first <see cref="DatabaseInstance.Dispose"/> or
    /// <see cref="DatabaseInstance.DisposeAsync"/>, after the database's own disposal core, on the
    /// thread that ran the close.
    /// </summary>
    /// <param name="database">A database of this engine whose close has ended.</param>
    /// <remarks>
    /// <para>
    /// The leaf compares references: a database the leaf already let go (dropped, replaced by its
    /// reopen after going offline, or closed by the engine's disposal) is not tracked any more,
    /// and a newer instance of the same name is never removed.
    /// </para>
    /// <para>
    /// <b>It must not wait for the leaf's lock while the leaf could hold it waiting for this close.</b>
    /// The leaf disposes a database it let go while holding its lock, and that disposal waits for a
    /// close a holder started; the leaf stops tracking such a database before it disposes it. A
    /// leaf therefore first reads its lock-free published snapshot of the databases it tracks and
    /// returns at once when the database is not in it, and otherwise takes its lock without
    /// waiting on it indefinitely (it re-reads the snapshot between attempts), then removes the
    /// database if it still tracks that instance.
    /// </para>
    /// </remarks>
    protected abstract void ForgetClosedDatabaseCore(DatabaseInstance database);

    /// <summary>
    /// Hands a database whose close has ended to the leaf (<see cref="ForgetClosedDatabaseCore"/>),
    /// and ends every worker's failure record of it: the entry <see cref="DatabaseInstance"/> calls
    /// once its close ran, whoever closed it (a holder, a drop, a reopen after it went offline, the
    /// engine's disposal).
    /// </summary>
    /// <param name="database">A database of this engine whose close has ended.</param>
    /// <remarks>
    /// The failures a worker counted belong to the instance that closed: a database reopened from its
    /// files counts its failures from one, so it is not taken offline on its first failure after a
    /// reopen, and a worker's failure on the closed instance no longer keeps the engine
    /// <see cref="EngineState.Faulted"/> (owner decision 25). A close always ends before a new
    /// instance of the same name is registered (the leaf waits for it), so this never ends a newer
    /// instance's record.
    /// </remarks>
    internal void ForgetClosedDatabase(DatabaseInstance database)
    {
        try
        {
            ForgetClosedDatabaseCore(database);
        }
        finally
        {
            ForgetWorkerFailures(database.Name);
            DatabaseEventSource.Log.DatabaseClosed(this, database.Name);
        }
    }

    /// <summary>
    /// Drops a database whose name and engine state <see cref="DropDatabaseAsync"/> checked.
    /// </summary>
    /// <param name="name">The name of the database; never empty.</param>
    /// <param name="cancellationToken">Not canceled when the call starts.</param>
    /// <returns>A task that completes once the database is dropped.</returns>
    protected abstract ValueTask DropDatabaseCoreAsync(DatabaseName name, CancellationToken cancellationToken);

    /// <summary>
    /// Enumerates the engine's databases; <see cref="GetDatabasesAsync"/> checked the engine state
    /// and the token when the call was made.
    /// </summary>
    /// <param name="cancellationToken">Cancels the enumeration; not canceled when the call starts.</param>
    /// <returns>An async sequence of the databases.</returns>
    protected abstract IAsyncEnumerable<DatabaseInstance> GetDatabasesCore(CancellationToken cancellationToken);

    /// <summary>
    /// Looks up an open database whose name and engine state <see cref="TryGetDatabase"/> checked.
    /// </summary>
    /// <param name="name">The name of the database; never empty.</param>
    /// <param name="database">When this method returns true, the database.</param>
    /// <returns>True when the database is open in the engine; otherwise false.</returns>
    protected abstract bool TryGetDatabaseCore(DatabaseName name, [MaybeNullWhen(false)] out DatabaseInstance database);

    /// <summary>
    /// Gets the storage error that took an open database offline, for a name and engine state
    /// <see cref="GetOfflineError"/> checked: the error of the first of the database's storages to
    /// go offline, or null when the database is online, not open, or closing.
    /// </summary>
    /// <param name="name">The name of the database; never empty.</param>
    /// <returns>The error, or null.</returns>
    protected abstract StorageOfflineException? GetOfflineErrorCore(DatabaseName name);

    /// <summary>
    /// Takes an open database offline because the engine gave up on it (owner decision 25 of
    /// 2026-10-06): a worker's failures of it lasted <see cref="WorkerFailureWindow"/> across at
    /// least <see cref="WorkerFailureMinimumPasses"/> failed passes in a row (owner decision 42 of
    /// 2026-10-07), or its journal passed the engine's cap while its checkpoints kept failing. The
    /// leaf takes the
    /// database's storages offline through
    /// <see cref="Storage.Storage.TakeOffline(StorageOfflineCause, string, Exception)"/>, so the
    /// database goes offline exactly as after a failed durable flush (#1243).
    /// </summary>
    /// <param name="name">The name of the database; never empty.</param>
    /// <param name="cause">The cause: the worker's, or <see cref="StorageOfflineCause.JournalSizeLimit"/>.</param>
    /// <param name="reason">What the engine gave up on, naming the worker, for the storage's message.</param>
    /// <param name="failure">The worker's last failure.</param>
    /// <returns>
    /// True when this call took the database offline; false when the database is not open, is
    /// closing, or is offline already: a failure of such a database does not count.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Called on a thread-pool thread, never a worker's: taking a storage offline latches its
    /// journal under the journal's lock, which a durable flush holds through its fsync, so the call
    /// may wait for a hung fsync of this database, and no worker waits with it (#1268's
    /// per-database isolation). The engine runs one call per database at a time, and none once its
    /// disposal started.
    /// </para>
    /// <para>
    /// The leaf must find the database without waiting for its registry lock, which a create, open
    /// or drop holds for as long as a recovery runs: the published, lock-free snapshot of its
    /// databases, as <see cref="ForgetClosedDatabaseCore"/> reads it, so a give-up on one database
    /// never waits for another's open.
    /// </para>
    /// </remarks>
    protected abstract bool TakeDatabaseOfflineCore(DatabaseName name, StorageOfflineCause cause, string reason, Exception failure);

    /// <summary>
    /// Gives up on a database for a worker this engine owns: queues the leaf's
    /// <see cref="TakeDatabaseOfflineCore"/> to the thread pool and returns at once. Once the leaf
    /// took the database offline, the engine ends every worker's failure record of it and writes
    /// the event. Nothing is queued on an engine whose disposal started, for a database name that is
    /// empty, or for a database whose give-up is still queued or running.
    /// </summary>
    /// <param name="worker">The worker whose work on the database kept failing.</param>
    /// <param name="database">The database's name, as the worker reported it.</param>
    /// <param name="cause">The cause.</param>
    /// <param name="reason">What the engine gave up on, for the storage's message.</param>
    /// <param name="failure">The worker's last failure.</param>
    /// <returns>True when this call queued the give-up; false when it queued nothing.</returns>
    /// <remarks>
    /// <para>
    /// The worker's thread never runs the leaf's core (the core's remarks say why), so the pass that
    /// gave up goes on to the engine's other databases at once. The database goes offline
    /// a moment later; until then the worker's record of it still backs it off, and a failure the
    /// worker reports meanwhile queues nothing more.
    /// </para>
    /// <para>
    /// A database whose close raced the give-up is not taken offline. Any other failure of the
    /// leaf's core is recorded on the worker (<see cref="DatabaseEngineWorker.Fault"/>, until its
    /// next pass runs to its end) as a failure of that database, not of the engine as a whole
    /// (owner decision 42 review): <see cref="HasFailingWorker"/> keeps naming the database, whose
    /// record stays, and <see cref="HasEngineWideFailure"/> does not turn true. The worker keeps
    /// running, and the database's next failure tries again.
    /// </para>
    /// </remarks>
    internal bool GiveUpOnDatabase(DatabaseEngineWorker worker, string database, StorageOfflineCause cause, string reason, Exception failure)
    {
        if (database.Length == 0 || IsDisposed)
        {
            return false;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_givingUp.TryAdd(database, completion.Task))
        {
            // The database's give-up is queued or running: one at a time.
            return false;
        }

        var request = new GiveUpRequest(this, worker, database, cause, reason, failure, completion);
        try
        {
            ThreadPool.UnsafeQueueUserWorkItem(static request => request.Engine.GiveUp(request), request, preferLocal: false);
        }
        catch
        {
            _givingUp.TryRemove(new KeyValuePair<string, Task>(database, completion.Task));
            completion.TrySetResult();
            throw;
        }

        return true;
    }

    // The queued give-up: the leaf takes the database offline, then the engine forgets the
    // workers' records of it and writes the event. It ends its entry whatever happens, and lets
    // nothing escape the thread-pool thread but an OutOfMemoryException.
    private void GiveUp(GiveUpRequest request)
    {
        try
        {
            bool taken;
            try
            {
                // An engine whose disposal started closes its databases itself.
                taken = !IsDisposed && TakeDatabaseOfflineCore(request.Database, request.Cause, request.Reason, request.Failure);
            }
            catch (ObjectDisposedException)
            {
                // The database closed under the call: nothing is left to take offline.
                taken = false;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                request.Worker.RecordGiveUpFailure(request.Database, exception);
                return;
            }

            if (taken)
            {
                // The engine reports the offline database now, not the worker (the class remarks).
                ForgetWorkerFailures(request.Database);
                DatabaseEventSource.Log.DatabaseTakenOffline(this, request.Worker, request.Database, request.Cause, request.Failure);
            }
        }
        finally
        {
            _givingUp.TryRemove(new KeyValuePair<string, Task>(request.Database, request.Completion.Task));
            request.Completion.TrySetResult();
        }
    }

    // Ends every attached worker's failure record of a database the engine no longer holds as it
    // was: taken offline, or closed.
    private void ForgetWorkerFailures(string database)
    {
        var workers = Volatile.Read(ref _workerView);
        for (int index = 0; index < workers.Count; index++)
        {
            workers[index].ForgetDatabase(database);
        }
    }

    /// <summary>
    /// Closes the leaf's databases and releases what the leaf owns, once the servers, the worker
    /// pumps and the workers are gone. Durably flushes every open database according to its
    /// storage's durability policy. Called once.
    /// </summary>
    /// <returns>A task that completes once the leaf's part of disposal is done.</returns>
    protected abstract ValueTask DisposeAsyncCore();

    // The worker pump frame: runs the worker until the engine stops it, and never lets the thread
    // end before that. DatabaseEngineWorker.Run returns only on cancellation and records every
    // failure itself; should it ever throw or return early, the run is recorded (the engine reports
    // Faulted until it is disposed), the pump sleeps the backoff and runs it again. Only an
    // OutOfMemoryException escapes the thread, which ends the process.
    private void Pump(DatabaseEngineWorker worker)
    {
        var cancellationToken = _stop.Token;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                worker.Run(cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                RecordWorkerRunFault(worker, new InvalidOperationException(
                    $"Worker '{worker.Name}' returned from Run before its engine stopped it; the engine runs it again."));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                RecordWorkerRunFault(worker, exception);
            }

            cancellationToken.WaitHandle.WaitOne(DatabaseEngineWorker.FailureBackoff);
        }
    }

    /// <summary>
    /// Records what ended a worker's <see cref="DatabaseEngineWorker.Run"/> before the engine stopped
    /// it, which keeps the engine <see cref="EngineState.Faulted"/> until it is disposed, and writes
    /// it (<c>WorkerLoopFaulted</c>). The pump's two exits call it; the base's loop lets neither
    /// happen, so the engine's tests reach it directly.
    /// </summary>
    /// <param name="worker">The worker whose run ended.</param>
    /// <param name="fault">What escaped the run, or the failure recorded for an early return.</param>
    internal void RecordWorkerRunFault(DatabaseEngineWorker worker, Exception fault)
    {
        Volatile.Write(ref _workerRunFault, fault);
        DatabaseEventSource.Log.WorkerLoopFaulted(this, worker, fault);
    }

    // The traced create (event-sources plan, D5 a): entered only while the source is enabled, so
    // the timestamp and the wrapper cost nothing while nobody listens. The filter writes a failure
    // and never catches it: the exception leaves exactly as it would untraced.
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<DatabaseInstance> CreateDatabaseTracedAsync(DatabaseName name, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        DatabaseInstance database;
        try
        {
            database = await CreateDatabaseCoreAsync(name, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (TraceDatabaseOperationFailure(name, createOperation, exception))
        {
            throw;
        }

        DatabaseEventSource.Log.DatabaseCreated(this, name, started);
        return database;
    }

    // The traced open: the event is written only for a database the leaf held no open instance of
    // before the call, so an open of an open database writes nothing.
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<DatabaseInstance> OpenDatabaseTracedAsync(DatabaseName name, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        bool heldOpen = HoldsOpenDatabase(name);
        var waitedForClose = new StrongBox<bool>();
        DatabaseInstance database;
        try
        {
            database = await OpenUnclosedAsync(name, waitedForClose, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (TraceDatabaseOperationFailure(name, openOperation, exception))
        {
            throw;
        }

        if (!heldOpen)
        {
            DatabaseEventSource.Log.DatabaseOpened(this, name, waitedForClose.Value, started);
        }

        return database;
    }

    // The traced drop.
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask DropDatabaseTracedAsync(DatabaseName name, CancellationToken cancellationToken)
    {
        try
        {
            await DropDatabaseCoreAsync(name, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (TraceDatabaseOperationFailure(name, dropOperation, exception))
        {
            throw;
        }

        DatabaseEventSource.Log.DatabaseDropped(this, name);
    }

    // Whether the leaf held an open instance of the database before a traced open: a read of the
    // leaf's lookup, made only while the source is enabled. A lookup that throws (a leaf's own name
    // or state check) reads as none; the open's core then runs as it would untraced and decides.
    private bool HoldsOpenDatabase(DatabaseName name)
    {
        try
        {
            return TryGetDatabaseCore(name, out var database) && !database.IsClosing;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }

    // An exception filter: writes a failed create, open or drop and returns false, so nothing is
    // caught. A cancellation is not a failure, nor is a missing database on open, which a server's
    // database resolution expects.
    private bool TraceDatabaseOperationFailure(DatabaseName name, string operation, Exception exception)
    {
        if (exception is not OperationCanceledException
            && !(exception is DatabaseNotFoundException && ReferenceEquals(operation, openOperation)))
        {
            DatabaseEventSource.Log.DatabaseOperationFailed(this, name, operation, exception);
        }

        return false;
    }

    // The open behind OpenDatabaseAsync: never hands out a closed instance. A database the core
    // returns while a holder closes it is waited for, and opened again once its close ended and
    // the leaf forgot it; ForgetClosedDatabaseCore runs before the close's waiters resume. A traced
    // open passes a box that learns whether the open waited for a close; an untraced one passes null.
    private async ValueTask<DatabaseInstance> OpenUnclosedAsync(DatabaseName name, StrongBox<bool>? waitedForClose, CancellationToken cancellationToken)
    {
        var database = await OpenDatabaseCoreAsync(name, cancellationToken).ConfigureAwait(false);
        while (database.IsClosing)
        {
            if (waitedForClose is not null)
            {
                waitedForClose.Value = true;
            }

            var closing = database;
            await closing.Closure.WaitAsync(cancellationToken).ConfigureAwait(false);
            ThrowIfDisposed();
            database = await OpenDatabaseCoreAsync(name, cancellationToken).ConfigureAwait(false);
            if (ReferenceEquals(database, closing))
            {
                // The leaf still tracks a database whose close ended: a leaf defect, reported
                // rather than retried forever.
                throw new InvalidOperationException(
                    $"Engine '{_name}' still tracks database '{name}' after its close ended; the engine did not forget it.");
            }
        }

        return database;
    }

    private void ThrowIfComposed()
    {
        if (_composed)
        {
            throw new InvalidOperationException("Engine composition is frozen; workers and servers attach only before it completes.");
        }
    }

    private static void ThrowIfEmpty(DatabaseName name)
    {
        if (name.IsEmpty)
        {
            throw new ArgumentException("A database name is required.", nameof(name));
        }
    }

    /// <summary>
    /// A give-up queued to the thread pool (<see cref="GiveUpOnDatabase"/>), and the completion the
    /// engine's disposal waits for.
    /// </summary>
    private sealed record GiveUpRequest(
        DatabaseEngine Engine,
        DatabaseEngineWorker Worker,
        string Database,
        StorageOfflineCause Cause,
        string Reason,
        Exception Failure,
        TaskCompletionSource Completion);
}
