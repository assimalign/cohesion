namespace Assimalign.Cohesion.Database.Storage.Internal;

/// <summary>
/// Where a journal frame lies: the LSN it carries, its byte offset in the journal since the last
/// truncation, and its length. A storage transaction that spills a page's pre-image keeps only
/// this and reads the full page image back from the journal (#1253); a checkpoint cannot
/// truncate the journal while the transaction is active, so the location stays valid until the
/// transaction completes.
/// </summary>
/// <param name="Lsn">The LSN of the record in the frame.</param>
/// <param name="Offset">The frame's offset in the journal, counted from the last truncation.</param>
/// <param name="Length">The frame's length in bytes, prefix and checksum included.</param>
internal readonly record struct StorageJournalLocation(long Lsn, long Offset, int Length);
