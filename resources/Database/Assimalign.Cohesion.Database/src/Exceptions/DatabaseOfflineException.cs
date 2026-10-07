using System;

using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// Thrown by every operation on a database that went offline: a durable flush of its journal
/// or of one of its data files failed (#1243), a write of a journal's append buffer failed (#1252),
/// a write of a file header failed after its header slot write was issued (#1268), or its engine
/// gave up on it because a background worker's work on it kept failing or its journal passed the
/// engine's cap (owner decision 25). Nothing more is written to the database, and
/// every later operation, in process and over every wire server, is refused with this
/// exception until the database is reopened (<see cref="DatabaseEngine.OpenDatabaseAsync"/>),
/// whose recovery reads the journal and decides the outcome of every commit that was not
/// confirmed.
/// </summary>
/// <remarks>
/// <para>
/// The storage's <see cref="StorageOfflineException.Cause"/>, on the inner exception, says which:
/// a device operation, or the background worker whose work the engine gave up on, or the journal
/// cap (<see cref="DatabaseEngine.WorkerFailureWindow"/>, owner decisions 25 of 2026-10-06 and 42
/// of 2026-10-07).
/// </para>
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

        return new DatabaseOfflineException(
            code,
            $"{code}: Database '{database}' is offline: {DescribeFailure(cause)}, so nothing " +
            "more is written to it. Every operation is refused until the database is reopened (OpenDatabaseAsync); the " +
            "reopen's recovery reads the journal and decides the outcome of every commit that was not confirmed.",
            cause);
    }

    /// <summary>
    /// Describes what took a storage offline, with the failure's own message, for an engine's
    /// message: <c>a durable flush of the data file of its storage failed (…)</c> for a device
    /// operation, <c>its checkpoints kept failing and its engine gave up on it (…)</c> when the
    /// engine gave up on the storage (owner decision 25).
    /// </summary>
    /// <param name="offline">The storage's offline error.</param>
    /// <returns>The clause.</returns>
    internal static string DescribeFailure(StorageOfflineException offline)
    {
        string detail = offline.InnerException?.Message ?? offline.Message;
        return offline.Cause is StorageOfflineCause.JournalFlush or StorageOfflineCause.DataFlush or StorageOfflineCause.HeaderWrite
            ? $"{Describe(offline.Cause)} of its storage failed ({detail})"
            : $"{Describe(offline.Cause)} ({detail})";
    }

    /// <summary>
    /// Describes what took a storage offline, for an engine's message: the root owns its own
    /// wording of the storage's cause (the layer that owns both vocabularies translates).
    /// </summary>
    /// <param name="cause">The storage's offline cause.</param>
    /// <returns>The phrase, such as <c>a durable flush of the data file</c>.</returns>
    /// <remarks>
    /// <see cref="StorageOfflineCause.JournalFlush"/> covers both a failed fsync of the journal
    /// (#1243) and a failed drain of its append buffer (#1252), so it reads as either; the inner
    /// storage exception's message names the exact operation. An engine's causes (owner decision
    /// 25) read as a whole clause, and the inner storage exception's message names the worker.
    /// </remarks>
    internal static string Describe(StorageOfflineCause cause) => cause switch
    {
        StorageOfflineCause.JournalFlush => "a write or flush of the journal",
        StorageOfflineCause.DataFlush => "a durable flush of the data file",
        StorageOfflineCause.HeaderWrite => "a write of the file header",
        StorageOfflineCause.CheckpointFailures => "its checkpoints kept failing and its engine gave up on it",
        StorageOfflineCause.PageWriteBackFailures => "its page write-backs kept failing and its engine gave up on it",
        StorageOfflineCause.WriteAheadFlushFailures => "its write-ahead flushes kept failing and its engine gave up on it",
        StorageOfflineCause.VersionPurgeFailures => "its version purge kept failing and its engine gave up on it",
        StorageOfflineCause.JournalSizeLimit => "its journal grew past its engine's limit while its checkpoints kept failing",
        _ => "a write",
    };
}
