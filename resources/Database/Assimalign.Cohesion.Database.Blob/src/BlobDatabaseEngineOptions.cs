using System;
using System.IO;

using Assimalign.Cohesion.Database.Blob.Internal;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Blob;

/// <summary>
/// Configures a blob database engine instance: values only, which the engine copies when it is
/// created or built.
/// </summary>
/// <remarks>
/// The options carry no engine name (B3 of the engine extensibility design): the engine is named
/// once, by the first argument of <c>AddBlob(name, …)</c>, <see cref="BlobDatabaseEngine.CreateBuilder(string)"/>
/// or <see cref="BlobDatabaseEngine.Create(string, BlobDatabaseEngineOptions)"/> (owner decision 52 of
/// 2026-10-09).
/// </remarks>
public sealed class BlobDatabaseEngineOptions
{
    /// <summary>
    /// Gets or sets how commits reach stable storage. When unset, opening a
    /// database selects <see cref="StorageCommitDurability.Synchronous"/> for
    /// durable backing and <see cref="StorageCommitDurability.None"/> otherwise.
    /// Explicit synchronous or grouped durability requires durable backing;
    /// an unsupported choice fails before the database becomes operational.
    /// </summary>
    public StorageCommitDurability? Durability { get; set; }

    /// <summary>
    /// Gets or sets the bounded window a grouped commit waits for the flush
    /// worker before flushing inline, and the flush worker's wake cadence.
    /// </summary>
    public TimeSpan GroupCommitWindow { get; set; } = TimeSpan.FromMilliseconds(5);

    /// <summary>
    /// Gets or sets the checkpoint time backstop: the longest a database whose journal received
    /// records waits for a checkpoint, which flushes its pages by its durability policy and
    /// truncates its journal. Defaults to 5 minutes, PostgreSQL's <c>checkpoint_timeout</c>;
    /// under load <see cref="CheckpointJournalSize"/> triggers checkpoints first. An idle
    /// database is not checkpointed by time.
    /// </summary>
    public TimeSpan CheckpointInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or sets the journal size, in bytes, that triggers a checkpoint: 256 MiB by default,
    /// zero to rely on <see cref="CheckpointInterval"/> alone. It bounds the journal's disk use
    /// (memory, for an in-memory database) and the work a recovery replays. Must not be negative.
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
    /// <para>
    /// An offline database refuses every operation with <c>COHDBB002</c> until it is reopened
    /// (<see cref="BlobDatabaseEngine.OpenDatabaseAsync(DatabaseName, System.Threading.CancellationToken)"/>),
    /// whose recovery reads its journal; a hosted engine is reopened by the hosting module with
    /// backoff. A pass that finishes the database's work ends the streak, and the window starts
    /// again at the next failure.
    /// </para>
    /// <para>
    /// The engine is <see cref="EngineState.Faulted"/> from a worker's first failure on any
    /// database until that failure is worked off or the database goes offline, but
    /// <see cref="BlobDatabaseServer"/> refuses only the failing database meanwhile (owner decision
    /// 42 of 2026-10-07): its handshakes and exchanges, with <c>COHDBB003</c>, while the engine's
    /// other databases are served. Before the decision the server refused every start, connection,
    /// handshake and operation while the engine was not <see cref="EngineState.Running"/>, so one
    /// database whose work kept failing made the whole server unavailable for the window.
    /// </para>
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
    /// Gets or sets the hard cap, in bytes, on a database's journal while its checkpoints keep
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
    /// Gets or sets the buffer pool capacity of each database, in bytes: a whole number of 8 KiB
    /// pages, at least 1 MiB. Defaults to 32 MiB (4,096 pages).
    /// </summary>
    /// <remarks>
    /// The pool allocates its page buffers as pages are first loaded, so a database costs up to
    /// this much memory, plus about 160 bytes of bookkeeping per resident page (0.6 MiB at 4,096
    /// pages), once it touched that many pages (#1254).
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
    /// versions below the oldest snapshot bound).
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
    /// When a root is provided, each database's files live in a directory of its own under it.
    /// When it is null, the databases are held in memory.
    /// </remarks>
    public FileSystemPath? RootPath { get; set; }

    /// <summary>Gets or sets a borrowed storage strategy that overrides RootPath when provided.</summary>
    /// <remarks>
    /// The engine owns storage returned by the strategy and does not dispose the strategy itself.
    /// Internal (concrete-types plan, D9): the strategy base is internal, and this assembly's tests
    /// set their fault-injecting and recording doubles here through the test-only grant.
    /// </remarks>
    internal BlobStorageStrategy? StorageStrategy { get; set; }

    /// <summary>
    /// Copies every option, the internal ones included, into a new object: what an engine keeps, so
    /// a later change to the caller's options cannot reach the running engine
    /// (<see cref="BlobDatabaseEngine.Create"/> and <see cref="BlobDatabaseEngineBuilder.BuildAsync"/>).
    /// </summary>
    /// <returns>The copy.</returns>
    /// <remarks>
    /// A new option is added here too: <c>BlobEngineDeclarationTests</c> sets every option to a
    /// value other than its default, checks that the engine kept each one, and counts the public ones.
    /// </remarks>
    internal BlobDatabaseEngineOptions Snapshot() => new()
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
