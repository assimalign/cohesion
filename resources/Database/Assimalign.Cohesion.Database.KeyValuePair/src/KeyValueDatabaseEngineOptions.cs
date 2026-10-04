using System;
using System.IO;

using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.KeyValuePair;

/// <summary>
/// Configures a key-value database engine instance.
/// </summary>
public sealed class KeyValueDatabaseEngineOptions
{
    /// <summary>
    /// Gets or sets the logical engine name.
    /// </summary>
    public string? EngineName { get; set; }

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
    /// checkpoint truncates at once rather than spreading over the next cycle, so the size is the
    /// journal's bound; a recovery replays about 20 ms per MB from a warm file cache
    /// (Storage DESIGN.md, "Measurements"), about 5 seconds at the bound.
    /// </remarks>
    public long CheckpointJournalSize { get; set; } = 256L * 1024 * 1024;

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
    /// file-based if a path is provided, or in-memory otherwise.
    /// </remarks>
    public IKeyValueStorageStrategy? StorageStrategy { get; set; }
}
