using System;
using System.IO;

using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Graph;

/// <summary>
/// Configures a graph database engine instance.
/// </summary>
public sealed class GraphDatabaseEngineOptions
{
    /// <summary>
    /// Gets or sets the logical engine name.
    /// </summary>
    public string? EngineName { get; set; }

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
    /// Gets or sets how many passes in a row the checkpoint, page write-back, write-ahead flush or
    /// version-purge worker may fail on one database before the engine takes that database offline
    /// (owner decision 25 of 2026-10-06). Defaults to <see cref="DatabaseEngine.DefaultWorkerFailureLimit"/>
    /// (ten, Neo4j's tolerance of failed checkpoints); at the workers' one-second retry a database
    /// whose work keeps failing goes offline about ten seconds after its first failure. Must be at
    /// least one.
    /// </summary>
    /// <remarks>
    /// An offline database refuses every operation with <c>COHDBG012</c> until it is reopened
    /// (<see cref="GraphDatabaseEngine.OpenDatabaseAsync(DatabaseName, System.Threading.CancellationToken)"/>),
    /// whose recovery reads its journal; a hosted engine is reopened by the hosting module with
    /// backoff. A pass that finishes the database's work clears the count.
    /// </remarks>
    public int WorkerFailureLimit { get; set; } = DatabaseEngine.DefaultWorkerFailureLimit;

    /// <summary>
    /// Gets or sets the hard cap, in bytes, on a database's journal while its checkpoints keep
    /// failing (owner decision 25 of 2026-10-06): a checkpoint that fails while the journal holds
    /// this much takes the database offline at once, before the journal fills the device. Zero,
    /// the default, sets it to four times <see cref="CheckpointJournalSize"/> (1 GiB at the default
    /// size, PostgreSQL's <c>max_wal_size</c> default), or to 1 GiB when that is zero. Must not be
    /// negative, and when set, not below <see cref="CheckpointJournalSize"/>.
    /// </summary>
    /// <remarks>
    /// Only a failed checkpoint is compared with the cap: a journal that grows while its checkpoints
    /// are deferred to running statements, or wait for a busy storage, never takes the database
    /// offline.
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
    /// set their durability, fault-injecting and recording doubles here through the test-only grant.
    /// </remarks>
    internal GraphStorageStrategy? StorageStrategy { get; set; }
}
