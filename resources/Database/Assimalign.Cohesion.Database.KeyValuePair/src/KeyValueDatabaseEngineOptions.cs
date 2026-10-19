using System;
using System.IO;

using Assimalign.Cohesion.Database.KeyValuePair.Internal;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.KeyValuePair;

/// <summary>
/// Configures a key-value database engine instance: values only, which the engine copies when it
/// is created or built.
/// </summary>
/// <remarks>
/// The options carry no engine name (B3 of the engine extensibility design): the engine is named
/// once, by the first argument of <c>AddKeyValue(name, …)</c>, <see cref="KeyValueDatabaseEngine.CreateBuilder(string)"/>
/// or <see cref="KeyValueDatabaseEngine.Create(string, KeyValueDatabaseEngineOptions)"/> (owner decision 52 of
/// 2026-10-09).
/// </remarks>
public sealed class KeyValueDatabaseEngineOptions
{
    /// <summary>
    /// Gets or sets how commits reach stable storage across every database this
    /// engine opens. When unset, each storage file set selects
    /// <see cref="StorageCommitDurability.Synchronous"/> if its backing supports
    /// durable flushes, or <see cref="StorageCommitDurability.None"/> otherwise.
    /// Explicit synchronous or grouped durability requires durable backing and is
    /// rejected when the database opens if that backing cannot provide it.
    /// Grouped commits share the engine's write-ahead flush worker; both durable
    /// modes acknowledge a commit only after its records reach stable storage.
    /// </summary>
    public StorageCommitDurability? Durability { get; set; }

    /// <summary>
    /// Gets or sets the bounded window a grouped commit waits for the flush worker
    /// before flushing inline itself. Also the flush worker's wake cadence.
    /// </summary>
    public TimeSpan GroupCommitWindow { get; set; } = TimeSpan.FromMilliseconds(5);

    /// <summary>
    /// Gets or sets the checkpoint time backstop: the longest a file set (data or catalog) whose
    /// journal received records waits for a checkpoint, which durably flushes its pages and
    /// truncates its journal. Defaults to 5 minutes, PostgreSQL's <c>checkpoint_timeout</c>;
    /// under load <see cref="CheckpointJournalSize"/> triggers checkpoints first. An idle file set
    /// is not checkpointed by time.
    /// </summary>
    public TimeSpan CheckpointInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or sets the journal size, in bytes, that triggers a checkpoint of a file set:
    /// 256 MiB by default, zero to rely on <see cref="CheckpointInterval"/> alone. It bounds the
    /// journal's disk use and the work a recovery replays. Must not be negative.
    /// </summary>
    /// <remarks>
    /// PostgreSQL triggers on WAL volume too (<c>max_wal_size</c>, 1 GB, about eight times its
    /// 128 MB <c>shared_buffers</c>). Cohesion keeps that ratio to its 32 MiB pool, and its sharp
    /// checkpoint truncates at once rather than spreading over the next cycle, so the size is close
    /// to the journal's bound: writes that land before the checkpoint runs overshoot it, and a
    /// single statement that journals more than the size overshoots it by that much. A recovery
    /// replays 5 to 8 ms per MB from a warm file cache in storage format 3 (Storage DESIGN.md,
    /// "Measurements (#1253)"), about two seconds at the bound. An in-memory database holds its
    /// journal in memory: up to the size, and briefly up to twice the size while the in-memory
    /// buffer doubles past it, until the checkpoint's truncation releases the buffer.
    /// </remarks>
    public long CheckpointJournalSize { get; set; } = 256L * 1024 * 1024;

    /// <summary>
    /// Gets or sets how long the checkpoint, page write-back, write-ahead flush or version-purge
    /// worker's failures of one database must persist before the engine takes that database offline
    /// (owner decisions 25 of 2026-10-06 and 42 of 2026-10-07), across at least
    /// <see cref="WorkerFailureMinimumPasses"/> failed passes in a row. Defaults to
    /// <see cref="DatabaseEngine.DefaultWorkerFailureWindow"/> (one hundred seconds, the window
    /// Neo4j's ten failures span at its ten-second checkpoint check). The window is time, measured
    /// from the first failed pass of the database's current streak, so every worker gives up about
    /// that long after its first failure whatever its pace: a failing checkpoint, page write-back or
    /// write-ahead flush after about a hundred seconds, a deferred undo on its tenth retry (about
    /// 102 s), and a version purge's full pass, retried a backoff after each failure rather than at the
    /// next <see cref="MaintenanceInterval"/> (owner decision 46), after about 101 s. Widen it to ride out a longer device outage, or narrow it
    /// to give up sooner. Must be positive and at most
    /// <see cref="DatabaseEngine.MaximumWorkerFailureWindow"/>.
    /// </summary>
    /// <remarks>
    /// An offline database refuses every operation with <c>COHDBK002</c> until it is reopened
    /// (<see cref="KeyValueDatabaseEngine.OpenDatabaseAsync(DatabaseName, System.Threading.CancellationToken)"/>),
    /// whose recovery reads its journal; a hosted engine is reopened by the hosting module with
    /// backoff. A pass that finishes the database's work ends the streak, and the window starts
    /// again at the next failure.
    /// </remarks>
    public TimeSpan WorkerFailureWindow { get; set; } = DatabaseEngine.DefaultWorkerFailureWindow;

    /// <summary>
    /// Gets or sets how many failed passes in a row a worker's failures of one database must span,
    /// besides lasting <see cref="WorkerFailureWindow"/>, before the engine takes that database
    /// offline (owner decision 42 of 2026-10-07). Defaults to
    /// <see cref="DatabaseEngine.DefaultWorkerFailureMinimumPasses"/> (three), so a worker that visits
    /// a failing database seldom, or whose one attempt outlasted the window, retries it before the
    /// engine gives up. Several failures one pass reports count once. Must be at least one.
    /// </summary>
    public int WorkerFailureMinimumPasses { get; set; } = DatabaseEngine.DefaultWorkerFailureMinimumPasses;

    /// <summary>
    /// Gets or sets the hard cap, in bytes, on a file set's journal while its checkpoints keep
    /// failing (owner decision 25 of 2026-10-06): the second checkpoint in a row that fails while the journal holds
    /// this much takes the database offline, before the journal fills the device. Zero,
    /// the default, sets it to four times <see cref="CheckpointJournalSize"/> (1 GiB at the default
    /// size, PostgreSQL's <c>max_wal_size</c> default), or to 1 GiB when that is zero. Must not be
    /// negative, and when set, not below <see cref="CheckpointJournalSize"/>.
    /// </summary>
    /// <remarks>
    /// Only failed checkpoints are compared with the cap, and one is not enough: a journal that grows
    /// while its checkpoints are deferred to running statements, or wait for a busy storage, never
    /// takes the database offline by itself, and one transient failure of such a journal is retried
    /// like any other.
    /// </remarks>
    public long JournalSizeLimit { get; set; }

    /// <summary>
    /// Gets or sets the buffer pool capacity of each database's data file set, in bytes: a whole
    /// number of 8 KiB pages, at least 1 MiB. Defaults to 32 MiB (4,096 pages). The catalog file
    /// set keeps a fixed 1 MiB pool.
    /// </summary>
    /// <remarks>
    /// The pool allocates its page buffers as pages are first loaded, so a database costs up to
    /// this much memory, plus the 1 MiB catalog pool and about 160 bytes of bookkeeping per
    /// resident page (0.6 MiB at 4,096 pages), once it touched that many pages (#1254).
    /// </remarks>
    public long BufferPoolCapacity { get; set; } = 32L * 1024 * 1024;

    /// <summary>
    /// Gets or sets the cadence of the engine's page write-back worker: how often a
    /// paced batch of dirty pages is written back between checkpoints.
    /// </summary>
    public TimeSpan PageWriteBackInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets or sets the maximum number of dirty pages the page write-back worker
    /// writes per pass, per storage file set.
    /// </summary>
    public int PageWriteBackBatchSize { get; set; } = 16;

    /// <summary>
    /// Gets or sets the cadence of the engine's maintenance workers: the MVCC
    /// version purge (aborted-writer undo retries and physical reclamation of
    /// versions below the oldest snapshot bound) and index maintenance
    /// (currently a documented stub — see docs/DESIGN.md).
    /// </summary>
    public TimeSpan MaintenanceInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Gets or sets the delay before the first retry of a rolled-back writer's undo that failed
    /// (#1226): 100 ms, doubling after each failed retry up to <see cref="MaintenanceInterval"/>.
    /// Internal: tests that drive the purge pass themselves set it out of their way.
    /// </summary>
    internal TimeSpan DeferredUndoRetryDelay { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Gets or sets the clock the engine measures <see cref="WorkerFailureWindow"/> on; the system
    /// clock when null. Internal: this assembly's tests set a clock they move by hand, so a test
    /// crosses the window without waiting for it (owner decision 42).
    /// </summary>
    internal TimeProvider? TimeProvider { get; set; }

    /// <summary>
    /// Gets or sets the root directory where per-database files are created.
    /// </summary>
    /// <remarks>
    /// When <see cref="StorageStrategy"/> is null and <see cref="RootPath"/> is provided,
    /// a file-based strategy is used automatically. When both are null, an in-memory
    /// strategy is used.
    /// </remarks>
    public FileSystemPath? RootPath { get; set; }

    /// <summary>
    /// Gets or sets the storage strategy for creating and opening database storage.
    /// </summary>
    /// <remarks>
    /// When null, the engine selects a default strategy based on <see cref="RootPath"/>:
    /// file-based if a path is provided, or in-memory otherwise. Internal (concrete-types plan,
    /// D9): the strategy base is internal, and this assembly's tests set their durability and
    /// fault-injecting doubles here through the test-only grant.
    /// </remarks>
    internal KeyValueStorageStrategy? StorageStrategy { get; set; }

    /// <summary>
    /// Copies every option, the internal ones included, into a new object: what an engine keeps, so
    /// a later change to the caller's options cannot reach the running engine
    /// (<see cref="KeyValueDatabaseEngine.Create"/> and <see cref="KeyValueDatabaseEngineBuilder.BuildAsync"/>).
    /// </summary>
    /// <returns>The copy.</returns>
    /// <remarks>
    /// A new option is added here too: <c>KeyValueEngineDeclarationTests</c> sets every option to a
    /// value other than its default, checks that the engine kept each one, and counts the public ones.
    /// </remarks>
    internal KeyValueDatabaseEngineOptions Snapshot() => new()
    {
        Durability = Durability,
        GroupCommitWindow = GroupCommitWindow,
        CheckpointInterval = CheckpointInterval,
        CheckpointJournalSize = CheckpointJournalSize,
        WorkerFailureWindow = WorkerFailureWindow,
        WorkerFailureMinimumPasses = WorkerFailureMinimumPasses,
        JournalSizeLimit = JournalSizeLimit,
        BufferPoolCapacity = BufferPoolCapacity,
        PageWriteBackInterval = PageWriteBackInterval,
        PageWriteBackBatchSize = PageWriteBackBatchSize,
        MaintenanceInterval = MaintenanceInterval,
        DeferredUndoRetryDelay = DeferredUndoRetryDelay,
        TimeProvider = TimeProvider,
        RootPath = RootPath,
        StorageStrategy = StorageStrategy,
    };
}
