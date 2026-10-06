using System;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

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
/// <para>
/// <b>Every engine's message leads with the model's offline code</b> (owner decision 24 of
/// 2026-10-06, #1272), whichever path raised it: a transaction's commit, explicit or the
/// autocommit transaction of one statement or operation
/// (<see cref="Create(string, string, TransactionCommitUnconfirmedException)"/>, which keeps the
/// kernel's exception as the inner exception); a statement that committed by itself, or a storage
/// bracket whose commit record was written before the flush failed
/// (<see cref="Create(string, string, StorageOfflineException)"/>). The codes are those of
/// <see cref="DatabaseOfflineException"/>: <c>COHSQLT004</c>, <c>COHDBK002</c>,
/// <c>COHDBD002</c>, <c>COHDBG012</c> and <c>COHDBB002</c>.
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
            $"{code}: Database '{database}' went offline while the operation was committing: {DatabaseOfflineException.Describe(cause.Cause)} of its " +
            $"storage failed ({detail}) after the operation's work reached the journal. The operation may or may not have been " +
            "applied; do not retry it. Reopen the database (OpenDatabaseAsync): its recovery keeps the work if its records " +
            "reached stable storage and discards it if not, and reading the data back then tells which.",
            cause);
    }

    /// <summary>
    /// Creates the exception an engine raises for a transaction's commit whose commit record was
    /// appended but could not be made durable: the transaction kernel's unconfirmed commit,
    /// translated at the model boundary with the model's code leading the message (#1272). The
    /// transaction ended committed in this process; the reopen's recovery keeps it exactly when
    /// its record reached stable storage.
    /// </summary>
    /// <param name="code">The model's offline code that leads the message, for example <c>COHDBD002</c>.</param>
    /// <param name="database">The database's name.</param>
    /// <param name="cause">The kernel's unconfirmed commit, which becomes the inner exception.</param>
    /// <returns>The exception to throw.</returns>
    public static DatabaseTransactionCommitUnconfirmedException Create(string code, string database, TransactionCommitUnconfirmedException cause)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentNullException.ThrowIfNull(cause);

        // The kernel's flush failure carries the storage's offline error when the storage raised
        // it; the root words its cause, as it does for every other offline message.
        string failure = StorageOfflineException.Find(cause) is { } offline
            ? $"{DatabaseOfflineException.Describe(offline.Cause)} of its storage failed ({offline.InnerException?.Message ?? offline.Message})"
            : $"the durable flush of its commit record failed ({cause.InnerException?.Message ?? cause.Message})";
        return new DatabaseTransactionCommitUnconfirmedException(
            $"{code}: Database '{database}' went offline while a transaction was committing: {failure} after the transaction's " +
            "commit record was written. The transaction may or may not have committed; do not retry it. Reopen the database " +
            "(OpenDatabaseAsync): its recovery keeps the commit if its record reached stable storage and discards it if not, and " +
            "reading the data back then tells which.",
            cause);
    }
}
