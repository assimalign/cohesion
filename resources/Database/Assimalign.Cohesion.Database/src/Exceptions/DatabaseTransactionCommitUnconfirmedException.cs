using System;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// Thrown by a commit whose record was written but could not be made durable. The commit's
/// outcome is unknown: the failed flush took the database offline (#1243), and its reopen's
/// recovery keeps the commit if the record reached stable storage and discards it if not.
/// </summary>
/// <remarks>
/// <para>
/// This is the area-root surface of the transaction kernel's
/// <c>TransactionCommitUnconfirmedException</c> (in
/// <c>Assimalign.Cohesion.Database.Transactions</c>), translated by the model engines at their
/// boundary like <see cref="DatabaseTransactionAbortedException"/>. The transaction ended as
/// committed in this process (its state is <c>Committed</c> and its locks are released), but
/// every later operation on the database is refused with
/// <see cref="DatabaseOfflineException"/> until it is reopened, so no session reads its effects
/// before recovery decides them.
/// </para>
/// <para>
/// Unlike an abort, it is not retryable: the transaction's work may be applied, and re-running
/// it in a new transaction could apply it twice. A caller that must know the outcome reads it
/// back after the database reopens, as after any commit whose acknowledgment was lost.
/// </para>
/// </remarks>
public class DatabaseTransactionCommitUnconfirmedException : DatabaseException
{
    /// <summary>
    /// Initializes a new <see cref="DatabaseTransactionCommitUnconfirmedException"/>.
    /// </summary>
    /// <param name="message">The error message.</param>
    public DatabaseTransactionCommitUnconfirmedException(string message)
        : base(message) { }

    /// <summary>
    /// Initializes a new <see cref="DatabaseTransactionCommitUnconfirmedException"/> with an inner exception.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The underlying cause.</param>
    public DatabaseTransactionCommitUnconfirmedException(string message, Exception? innerException)
        : base(message, innerException) { }
}
