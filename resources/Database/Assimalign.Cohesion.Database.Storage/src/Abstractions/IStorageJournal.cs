using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Storage;

/// <summary>
/// The write-ahead log (WAL) for a storage file: the single durability mechanism
/// of the storage layer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Write ordering rules.</b> Appends are serialized: log sequence numbers (LSNs)
/// are strictly monotonic and match the physical order of records in the journal
/// stream. A transaction's first page modification appends the page's before-image;
/// commit appends the after-image of every modified page followed by the commit
/// record, which must be durable (<see cref="EnsureDurable"/>) before the commit is
/// acknowledged — the write-ahead rule. A page may be written to the data file only
/// after the journal is durable up to that page's LSN, which the buffer pool enforces
/// through the same <see cref="EnsureDurable"/> gate.
/// </para>
/// <para>
/// <b>Recovery.</b> On open, recovery replays the journal: committed after-images
/// are redone, and before-images of transactions without a durable commit record are
/// applied to undo stolen writes. Corrupted or torn records at the tail of the
/// journal terminate the scan and are ignored — they belong to work that was never
/// acknowledged.
/// </para>
/// </remarks>
public interface IStorageJournal : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Gets the LSN of the most recently appended record, or zero when the journal
    /// is empty.
    /// </summary>
    long LastLsn { get; }

    /// <summary>
    /// Gets the LSN up to which the journal is known durable. Records with LSNs
    /// beyond this value may be lost on a crash.
    /// </summary>
    long DurableLsn { get; }

    /// <summary>
    /// Appends a transaction-begin record.
    /// </summary>
    /// <param name="transactionSequence">The storage-level transaction sequence.</param>
    /// <returns>The assigned LSN.</returns>
    long AppendBegin(long transactionSequence);

    /// <summary>
    /// Appends a full page image (before or after a transaction's modifications).
    /// </summary>
    /// <param name="transactionSequence">The storage-level transaction sequence.</param>
    /// <param name="pageId">The page the image describes.</param>
    /// <param name="type">
    /// <see cref="JournalRecordType.BeforePageImage"/> or <see cref="JournalRecordType.AfterPageImage"/>.
    /// </param>
    /// <param name="image">The full page buffer.</param>
    /// <returns>The assigned LSN.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="type"/> is not a page-image type.</exception>
    long AppendPageImage(long transactionSequence, PageId pageId, JournalRecordType type, ReadOnlySpan<byte> image);

    /// <summary>
    /// Appends an opaque logical operation record on behalf of a higher layer.
    /// </summary>
    /// <param name="transactionSequence">The storage-level transaction sequence.</param>
    /// <param name="payload">The logical payload.</param>
    /// <returns>The assigned LSN.</returns>
    long AppendOperation(long transactionSequence, ReadOnlySpan<byte> payload);

    /// <summary>
    /// Appends a transaction-commit record. The caller must make the record durable
    /// with <see cref="EnsureDurable"/> before acknowledging the commit.
    /// </summary>
    /// <param name="transactionSequence">The storage-level transaction sequence.</param>
    /// <returns>The assigned LSN.</returns>
    long AppendCommit(long transactionSequence);

    /// <summary>
    /// Appends a transaction-rollback record.
    /// </summary>
    /// <param name="transactionSequence">The storage-level transaction sequence.</param>
    /// <returns>The assigned LSN.</returns>
    long AppendRollback(long transactionSequence);

    /// <summary>
    /// Truncates the journal and writes a fresh checkpoint record. The caller must
    /// have durably flushed all page state to the data file first — after this call
    /// the discarded records can no longer drive recovery.
    /// </summary>
    /// <param name="activeTransactions">The sequences of transactions active at checkpoint time.</param>
    /// <returns>The LSN of the checkpoint record (LSNs continue monotonically across truncation).</returns>
    long Checkpoint(ReadOnlySpan<long> activeTransactions);

    /// <summary>
    /// Guarantees the journal is durable up to and including the given LSN,
    /// flushing if necessary.
    /// </summary>
    /// <param name="lsn">The LSN that must be durable.</param>
    void EnsureDurable(long lsn);

    /// <summary>
    /// Flushes buffered journal data.
    /// </summary>
    /// <param name="forceDurable">When true, requests durable flush semantics where supported.</param>
    void Flush(bool forceDurable = false);

    /// <summary>
    /// Reads all valid records from the journal in LSN order. A corrupted or torn
    /// tail terminates the scan.
    /// </summary>
    /// <returns>The decoded record list.</returns>
    IReadOnlyList<JournalRecord> ReadAll();
}
