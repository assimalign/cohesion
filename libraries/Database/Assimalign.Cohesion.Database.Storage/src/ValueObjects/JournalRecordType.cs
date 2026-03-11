namespace Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Represents the type of an entry in the journal.
/// </summary>
public enum JournalRecordType : byte
{
    /// <summary>
    /// Marks the start of a transaction.
    /// </summary>
    BeginTransaction = 1,

    /// <summary>
    /// Represents a logical data operation that can be replayed.
    /// </summary>
    Operation = 2,

    /// <summary>
    /// Marks successful transaction commit.
    /// </summary>
    CommitTransaction = 3,

    /// <summary>
    /// Marks transaction rollback.
    /// </summary>
    RollbackTransaction = 4,

    /// <summary>
    /// Optional checkpoint marker.
    /// </summary>
    Checkpoint = 5,
}
