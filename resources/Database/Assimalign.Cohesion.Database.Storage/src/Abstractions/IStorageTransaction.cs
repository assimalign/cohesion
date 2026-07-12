using System;

namespace Assimalign.Cohesion.Database.Storage;

/// <summary>
/// A storage-level transaction scope: the unit of atomicity and durability for
/// record mutations against a storage file.
/// </summary>
/// <remarks>
/// <para>
/// Mutations made through a transaction are staged in the buffer pool and protected
/// by the write-ahead log: the first modification of each page journals its before
/// image, and <see cref="Commit"/> journals the after image of every modified page
/// followed by a commit record that is durable before the call returns. Data pages
/// are <i>not</i> forced to disk at commit — recovery replays committed changes from
/// the journal (no-force), and uncommitted changes that reached disk early are
/// undone from before images (steal).
/// </para>
/// <para>
/// Pages modified by an active transaction are write-locked to that transaction
/// until it completes; a second transaction touching the same page fails rather
/// than waits. Fine-grained (record-level) concurrency control is the transaction
/// layer's responsibility (<c>Database.Transactions</c>), built above this scope.
/// </para>
/// <para>
/// Disposing an active transaction rolls it back.
/// </para>
/// </remarks>
public interface IStorageTransaction : IDisposable
{
    /// <summary>
    /// Gets the storage-level transaction sequence. Sequences are monotonic within
    /// a storage instance and identify the transaction in the journal.
    /// </summary>
    long Sequence { get; }

    /// <summary>
    /// Gets a value indicating whether the transaction is still active (neither
    /// committed nor rolled back).
    /// </summary>
    bool IsActive { get; }

    /// <summary>
    /// Commits the transaction: journals after images of every modified page and a
    /// commit record, and returns only after the journal is durable up to the
    /// commit record.
    /// </summary>
    /// <exception cref="StorageTransactionException">The transaction is not active.</exception>
    void Commit();

    /// <summary>
    /// Rolls the transaction back: restores every modified page to its before image
    /// in the buffer pool and journals a rollback record.
    /// </summary>
    /// <exception cref="StorageTransactionException">The transaction is not active.</exception>
    void Rollback();
}
