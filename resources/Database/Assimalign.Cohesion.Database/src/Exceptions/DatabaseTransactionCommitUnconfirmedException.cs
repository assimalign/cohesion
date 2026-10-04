using System;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// Thrown by a commit whose record was written but could not be made durable. The
/// transaction is committed (its state is <c>Committed</c>, its effects are visible and its
/// locks are released); only its durability is unconfirmed, and it is lost if the database
/// stops before its log is next flushed durably.
/// </summary>
/// <remarks>
/// This is the area-root surface of the transaction kernel's
/// <c>TransactionCommitUnconfirmedException</c> (in
/// <c>Assimalign.Cohesion.Database.Transactions</c>), translated by the model engines at their
/// boundary like <see cref="DatabaseTransactionAbortedException"/>. Unlike an abort, it is not
/// retryable: the transaction's work is applied, and re-running it in a new transaction could
/// apply it twice. A caller that must know the outcome reads it back after the database
/// reopens, as after any commit whose acknowledgment was lost.
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
