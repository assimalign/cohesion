using System;

namespace Assimalign.Cohesion.Database.Transactions;

/// <summary>
/// Thrown when a transaction's commit record was written to the log but could not be made
/// durable. The transaction ended as committed in this process (it can no longer abort, and its
/// locks are released), but its outcome is unknown: for a journal-bound log the failed flush
/// took the storage offline (#1243), so nothing more is written, and the reopen's recovery keeps
/// the commit if its record reached stable storage and discards it if not.
/// </summary>
/// <remarks>
/// <para>
/// A written commit record cannot be taken back. Recovery classifies a transaction as
/// committed when its commit record is in the journal, and a checkpoint that truncates the
/// journal after the record presumes it committed. Reporting such a transaction as aborted and
/// undoing it would therefore be false whenever the record reaches stable storage later, and
/// an undo that has to wait for a retry would leave the effects in place for recovery to read
/// as committed. The outcome is the one a crash decides: committed if the record survives,
/// and nothing at all if it does not. Because a failed fsync may have dropped the record, every
/// later journal append, flush and checkpoint is refused
/// (<c>Assimalign.Cohesion.Database.Storage.StorageOfflineException</c>, the inner exception):
/// no later commit is acknowledged on top of an outcome nobody knows, as PostgreSQL treats a
/// failure in its commit critical section as <c>PANIC</c>
/// (<c>RecordTransactionCommit</c>, <c>src/backend/access/transam/xact.c:1470-1583</c>).
/// </para>
/// <para>
/// Unlike <see cref="TransactionAbortedException"/>, this is not retryable by construction:
/// re-running the transaction's work could apply it twice. It is an independent exception
/// root, like <see cref="TransactionAbortedException"/>; a model engine wraps it in the area's
/// <c>DatabaseTransactionCommitUnconfirmedException</c> at its boundary.
/// </para>
/// </remarks>
public class TransactionCommitUnconfirmedException : Exception
{
    /// <summary>
    /// Initializes a new <see cref="TransactionCommitUnconfirmedException"/>.
    /// </summary>
    /// <param name="message">The error message.</param>
    public TransactionCommitUnconfirmedException(string message)
        : base(message) { }

    /// <summary>
    /// Initializes a new <see cref="TransactionCommitUnconfirmedException"/> with an inner exception.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The failure of the durable flush.</param>
    public TransactionCommitUnconfirmedException(string message, Exception? innerException)
        : base(message, innerException) { }
}
