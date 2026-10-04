using System;

using Assimalign.Cohesion.Database.Storage;

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

    /// <summary>
    /// Creates the exception an engine raises for an operation that committed by itself, outside
    /// a transaction's commit, and whose storage commit record was written before the durable
    /// flush that took the database offline failed (<see cref="StorageOfflineException.CommitRecordWritten"/>),
    /// or for a self-committing statement that may already have committed part of its work when
    /// the database went offline. Either way the effect survives the reopen exactly when its
    /// records reached stable storage.
    /// </summary>
    /// <param name="code">The model's offline code that leads the message, for example <c>COHSQLT004</c>.</param>
    /// <param name="database">The database's name.</param>
    /// <param name="cause">The storage's offline error.</param>
    /// <returns>The exception to throw.</returns>
    public static DatabaseTransactionCommitUnconfirmedException Create(string code, string database, StorageOfflineException cause)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentNullException.ThrowIfNull(cause);

        string detail = cause.InnerException?.Message ?? cause.Message;
        return new DatabaseTransactionCommitUnconfirmedException(
            $"{code}: Database '{database}' went offline while the operation was committing: {cause.FailedOperation} of its " +
            $"storage failed ({detail}) after the operation's work reached the journal. The operation may or may not have been " +
            "applied; do not retry it. Reopen the database (OpenDatabaseAsync): its recovery keeps the work if its records " +
            "reached stable storage and discards it if not, and reading the data back then tells which.",
            cause);
    }
}
