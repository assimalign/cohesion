namespace Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Identifies the kind of a journal (write-ahead log) record.
/// </summary>
/// <remarks>
/// <para>
/// Values are persisted on disk, so they are append-only and never renumbered. Values 6 and 7
/// were the before-image and after-image of storage formats 1 and 2, which journaled two full
/// 8 KiB images of every page each storage transaction touched; storage format 3 (#1253)
/// retired them, and they are never reused.
/// </para>
/// <para>
/// The three page records of format 3 split by what recovery may do with them:
/// <see cref="FullPageImage"/> is the page as it stood before a transaction changed it and is
/// applied whatever became of that transaction; <see cref="PageDelta"/> and
/// <see cref="CommittedPageImage"/> describe a transaction's changes and are applied only when its
/// commit record is in the journal.
/// </para>
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
    /// page records drive redo.
    /// </summary>
    Operation = 2,

    /// <summary>
    /// Marks a transaction as committed. A transaction's page changes are recovered
    /// only when its commit record is durable.
    /// </summary>
    CommitTransaction = 3,

    /// <summary>
    /// Marks a transaction as rolled back.
    /// </summary>
    RollbackTransaction = 4,

    /// <summary>
    /// A checkpoint marker: all page state up to this point is durable in the data
    /// file, and recovery starts here. The payload carries the sequences of
    /// transactions active at checkpoint time.
    /// </summary>
    Checkpoint = 5,

    /// <summary>
    /// The full image of a page as it stood before a transaction's first change to it since the
    /// last checkpoint (or, past the transaction's pre-image budget, since any record): the redo
    /// base of every later change to the page. Recovery restores it unconditionally, whatever
    /// became of the transaction that wrote it, because it is committed content; that is also what
    /// repairs a page whose write a crash tore. The payload is the page's non-zero byte runs (its
    /// free gap elided); the LSN and checksum fields are not carried.
    /// </summary>
    FullPageImage = 8,

    /// <summary>
    /// The bytes a committed transaction changed in a page, appended at commit time: the LSN of the
    /// record the change applies on top of (the page's base LSN), then the changed byte runs.
    /// Recovery applies it only when the transaction's commit record is in the journal and the
    /// rebuilt page carries exactly the base LSN; any other LSN is a gap in the page's chain, which
    /// fails recovery as corruption.
    /// </summary>
    PageDelta = 9,

    /// <summary>
    /// The full image of a page after a committed transaction's changes, appended at commit time in
    /// place of a <see cref="PageDelta"/> when the delta would exceed about half a page and the image
    /// is not longer by more than a few run headers (a rewritten Blob page, a page a delete cleared):
    /// the page's base LSN, then its non-zero byte runs. Like a delta, and unlike a
    /// <see cref="FullPageImage"/>, recovery applies it only when the transaction's commit record is
    /// in the journal and the rebuilt page carries the base LSN.
    /// </summary>
    CommittedPageImage = 10,
}
