using System;

using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// Thrown by every operation on a database that went offline: a durable flush of its journal
/// or of one of its data files failed (#1243), or a write of a file header failed after its header
/// slot write was issued (#1268). Nothing more is written to the database, and
/// every later operation, in process and over every wire server, is refused with this
/// exception until the database is reopened (<see cref="IDatabaseEngine.OpenDatabaseAsync"/>),
/// whose recovery reads the journal and decides the outcome of every commit that was not
/// confirmed.
/// </summary>
/// <remarks>
/// <para>
/// A failed fsync leaves every record written since the last successful one in an unknown
/// state, and a retry can report success for writes the operating system already dropped
/// (PostgreSQL's 2018 "fsyncgate"). PostgreSQL therefore stops the server on a failed WAL
/// fsync (<c>issue_xlog_fsync</c>, <c>src/backend/access/transam/xlog.c:9877-9937</c>), treats a
/// failure in the commit critical section the same way (<c>RecordTransactionCommit</c>,
/// <c>src/backend/access/transam/xact.c:1470-1583</c>), and with <c>data_sync_retry</c> off panics
/// on a failed data-file fsync (<c>data_sync_elevel</c>, <c>src/backend/storage/file/fd.c:3966-3987</c>);
/// its restart runs crash recovery. A Cohesion database goes offline instead, and its reopen runs
/// the same recovery.
/// </para>
/// <para>
/// The commit whose flush failed is reported to its caller as
/// <see cref="DatabaseTransactionCommitUnconfirmedException"/>; this exception is what every
/// operation after it gets. The message leads with the model's code (<see cref="Code"/>):
/// <c>COHSQLT004</c> (SQL), <c>COHDBK002</c> (key-value), <c>COHDBD002</c> (documents),
/// <c>COHDBG012</c> (graph) and <c>COHDBB002</c> (blob). The storage's own
/// <c>COHDBS002</c> error is the inner exception.
/// </para>
/// </remarks>
public class DatabaseOfflineException : DatabaseException
{
    /// <summary>
    /// Initializes a new <see cref="DatabaseOfflineException"/>.
    /// </summary>
    /// <param name="code">The model's code that leads the message.</param>
    /// <param name="message">The error message, which leads with <paramref name="code"/>.</param>
    /// <param name="innerException">The storage failure that took the database offline.</param>
    public DatabaseOfflineException(string code, string message, Exception? innerException)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    /// <summary>
    /// Gets the model's code that leads the message.
    /// </summary>
    public string Code { get; }

    /// <summary>
    /// Creates the refusal an engine raises for an operation on an offline database.
    /// </summary>
    /// <param name="code">The model's code, for example <c>COHDBD002</c>.</param>
    /// <param name="database">The database's name.</param>
    /// <param name="cause">The storage's offline error.</param>
    /// <returns>The exception to throw.</returns>
    public static DatabaseOfflineException Create(string code, string database, StorageOfflineException cause)
    {
        ArgumentNullException.ThrowIfNull(cause);

        string detail = cause.InnerException?.Message ?? cause.Message;
        return new DatabaseOfflineException(
            code,
            $"{code}: Database '{database}' is offline: {cause.FailedOperation} of its storage failed ({detail}), so nothing " +
            "more is written to it. Every operation is refused until the database is reopened (OpenDatabaseAsync); the " +
            "reopen's recovery reads the journal and decides the outcome of every commit that was not confirmed.",
            cause);
    }
}
