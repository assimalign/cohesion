using System;

using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Documents;

/// <summary>
/// Configures a document database engine instance.
/// </summary>
public sealed class DocumentDatabaseEngineOptions
{
    /// <summary>
    /// Gets or sets the logical engine name.
    /// </summary>
    public string? EngineName { get; set; }

    /// <summary>
    /// Gets or sets the underlying storage's physical-commit durability policy.
    /// The default is <see cref="StorageCommitDurability.Synchronous"/>.
    /// Document mutations use nondurable physical brackets followed by the shared
    /// coordinator's synchronous durable logical commit. Logical document commits
    /// therefore remain synchronous even when this setting is
    /// <see cref="StorageCommitDurability.Grouped"/>; they do not currently use
    /// the storage group-commit gate.
    /// </summary>
    public StorageCommitDurability Durability { get; set; } = StorageCommitDurability.Synchronous;

    /// <summary>
    /// Gets or sets the underlying storage's physical group-commit wait window
    /// and the flush worker's wake cadence. Logical document commits flush
    /// synchronously through the transaction coordinator and do not wait on it.
    /// </summary>
    public TimeSpan GroupCommitWindow { get; set; } = TimeSpan.FromMilliseconds(5);

    /// <summary>
    /// Gets or sets the cadence of the engine's checkpoint worker: how often each
    /// open database's file set (metadata and chunks) are durably flushed and their
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
    /// Gets or sets the cadence of the engine's maintenance workers: the MVCC
    /// version purge (aborted-writer undo retries and physical reclamation of
    /// versions below the oldest snapshot bound).
    /// </summary>
    public TimeSpan MaintenanceInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Gets or sets the root directory where per-database files are created.
    /// </summary>
    /// <remarks>
    /// When <see cref="RootPath"/> is provided,
    /// a file-based strategy is used automatically. When it is null, an in-memory
    /// strategy is used.
    /// </remarks>
    public string? RootPath { get; set; }

}
