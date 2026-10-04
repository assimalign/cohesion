using System;

namespace Assimalign.Cohesion.Database.Transactions;

/// <summary>
/// Thrown when a transaction's commit record was written to the log but could not be made
/// durable. The transaction is committed: it can no longer abort, its effects are visible and
/// its locks are released. Only its durability is unconfirmed; it is lost if the process stops
/// before the log is next flushed durably.
/// </summary>
/// <remarks>
/// <para>
/// A written commit record cannot be taken back. Recovery classifies a transaction as
/// committed when its commit record is in the journal, and a checkpoint that truncates the
/// journal after the record presumes it committed. Reporting such a transaction as aborted and
/// undoing it would therefore be false whenever the record reaches stable storage later, and
/// an undo that has to wait for a retry would leave the effects in place for recovery to read
/// as committed. The outcome the caller gets is the one a crash would decide: committed if
/// the record survives, and nothing at all if it does not. A later transaction cannot be
/// durable without it: its own commit record follows this one in the journal, and a flush
/// covers everything before the record it flushes.
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
