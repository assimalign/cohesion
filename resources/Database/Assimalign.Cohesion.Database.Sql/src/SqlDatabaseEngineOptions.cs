using System;

using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Sql;

/// <summary>
/// Configures a SQL database engine instance.
/// </summary>
public sealed class SqlDatabaseEngineOptions
{
    /// <summary>
    /// Gets or sets the logical engine name.
    /// </summary>
    public string? EngineName { get; set; }

    /// <summary>
    /// Gets or sets how commits reach stable storage across every database this
    /// engine opens. The default, <see cref="StorageCommitDurability.Synchronous"/>,
    /// flushes the journal durably inside each commit;
    /// <see cref="StorageCommitDurability.Grouped"/> batches concurrent commits
    /// behind the engine's write-ahead flush worker so they share one durable flush.
    /// Both modes acknowledge a commit only after its records are durable.
    /// </summary>
    public StorageCommitDurability Durability { get; set; } = StorageCommitDurability.Synchronous;

    /// <summary>
    /// Gets or sets the bounded window a grouped commit waits for the flush worker
    /// before flushing inline itself. Also the flush worker's wake cadence.
    /// </summary>
    public TimeSpan GroupCommitWindow { get; set; } = TimeSpan.FromMilliseconds(5);

    /// <summary>
    /// Gets or sets the cadence of the engine's checkpoint worker: how often each
    /// open database's file sets (data and catalog) are durably flushed and their
    /// journals truncated.
    /// </summary>
    public TimeSpan CheckpointInterval { get; set; } = TimeSpan.FromSeconds(30);

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
    /// Gets or sets the cadence of the engine's maintenance workers (version purge
    /// and index maintenance — currently documented stubs, see docs/DESIGN.md).
    /// </summary>
    public TimeSpan MaintenanceInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Gets or sets the root directory where per-database files are created.
    /// </summary>
    /// <remarks>
    /// When <see cref="StorageStrategy"/> is null and <see cref="RootPath"/> is provided,
    /// a file-based strategy is used automatically. When both are null, an in-memory
    /// strategy is used.
    /// </remarks>
    public string? RootPath { get; set; }

    /// <summary>
    /// Gets or sets the storage strategy for creating and opening database storage.
    /// </summary>
    /// <remarks>
    /// When null, the engine selects a default strategy based on <see cref="RootPath"/>:
    /// file-based if a path is provided, or in-memory otherwise.
    /// </remarks>
    public ISqlStorageStrategy? StorageStrategy { get; set; }
}
