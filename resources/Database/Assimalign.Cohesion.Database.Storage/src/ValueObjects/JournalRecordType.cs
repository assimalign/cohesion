namespace Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Identifies the kind of a journal (write-ahead log) record.
/// </summary>
/// <remarks>
/// Values are persisted on disk, so they are append-only and never renumbered.
/// </remarks>
public enum JournalRecordType : byte
{
    /// <summary>
    /// Marks the start of a transaction.
    /// </summary>
    BeginTransaction = 1,

    /// <summary>
    /// A logical operation payload appended by a higher layer (the transaction or
    /// model layer). Logical records are opaque to storage recovery — physical
    /// page images drive redo/undo.
    /// </summary>
    Operation = 2,

    /// <summary>
    /// Marks a transaction as committed. A transaction's effects are recovered
    /// only when its commit record is durable.
    /// </summary>
    CommitTransaction = 3,

    /// <summary>
    /// Marks a transaction as rolled back.
    /// </summary>
    RollbackTransaction = 4,

    /// <summary>
    /// A checkpoint marker: all page state up to this point is durable in the data
    /// file, and recovery may start here. The payload carries the sequences of
    /// transactions active at checkpoint time.
    /// </summary>
    Checkpoint = 5,

    /// <summary>
    /// The full image of a page as it was before a transaction's first modification.
    /// Applied during recovery to undo the effects of transactions that never
    /// committed (including stolen writes that reached the data file early).
    /// </summary>
    BeforePageImage = 6,

    /// <summary>
    /// The full image of a page after a transaction's modifications, appended at
    /// commit time. Applied during recovery to redo committed changes that had not
    /// reached the data file.
    /// </summary>
    AfterPageImage = 7,
}
