using System;

namespace Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Raised when a storage, or its journal, is offline: a durable flush of its journal or of its
/// data file failed, and nothing may be written to either file again until the storage is
/// reopened, whose recovery reads the journal and decides what it holds (#1243).
/// </summary>
/// <remarks>
/// <para>
/// A failed fsync leaves the outcome of every record written since the last successful one
/// unknown: the operating system may have kept the bytes, or dropped them and marked the pages
/// clean, so a retry can report success for writes that never reached the device (PostgreSQL's
/// 2018 "fsyncgate"). PostgreSQL therefore stops the server on a WAL fsync failure
/// (<c>issue_xlog_fsync</c> raises <c>PANIC</c>, <c>src/backend/access/transam/xlog.c:9877-9937</c>),
/// treats a failure inside the commit critical section the same way
/// (<c>RecordTransactionCommit</c>, <c>src/backend/access/transam/xact.c:1470-1583</c>), and with
/// <c>data_sync_retry</c> off (its default) panics on a data-file fsync failure too, because a
/// later fsync "might falsely report success" (<c>data_sync_elevel</c>,
/// <c>src/backend/storage/file/fd.c:3966-3987</c>). A storage here goes offline instead of stopping
/// the process: the failing call throws this exception, and so does every later journal append,
/// flush, checkpoint, page write-back and header write of the same instance.
/// </para>
/// <para>
/// The message leads with <see cref="ErrorCode"/>; <see cref="Exception.InnerException"/> is the
/// failure that took the storage offline.
/// </para>
/// </remarks>
public sealed class StorageOfflineException : StorageException
{
    /// <summary>
    /// The code that leads the message: the storage is offline after a failed durable flush.
    /// </summary>
    public const string ErrorCode = "COHDBS002";

    /// <summary>
    /// Initializes a new <see cref="StorageOfflineException"/>.
    /// </summary>
    /// <param name="message">The message, which leads with <see cref="ErrorCode"/>.</param>
    /// <param name="cause">The durable flush failure that took the storage offline.</param>
    internal StorageOfflineException(string message, Exception cause)
        : base(message, cause)
    {
    }

    /// <summary>
    /// Gets whether the storage commit record of the operation that threw this exception was
    /// appended before the durable flush failed. Such a commit ended committed in memory and is
    /// decided by the reopen's recovery: it survives exactly when the record reached stable
    /// storage. An engine reports it as unconfirmed, never as refused, because a caller told
    /// "refused" would not look for effects that can survive (#1243).
    /// </summary>
    /// <remarks>
    /// Only a storage bracket that commits durably by itself raises it (a catalog write, or a
    /// self-committing statement's bracket); a refusal of a later operation is never flagged.
    /// </remarks>
    public bool CommitRecordWritten { get; private init; }

    /// <summary>
    /// Finds the <see cref="StorageOfflineException"/> in a failure: the failure itself, an
    /// exception in its chain of inner exceptions, or one an <see cref="AggregateException"/>
    /// on that chain carries.
    /// </summary>
    /// <param name="error">The failure to search, or null.</param>
    /// <returns>The first offline exception found, or null when there is none.</returns>
    public static StorageOfflineException? Find(Exception? error)
    {
        // A bounded walk: a pathological inner-exception cycle cannot loop forever.
        for (int depth = 0; error is not null && depth < 64; depth++)
        {
            if (error is StorageOfflineException offline)
            {
                return offline;
            }

            if (error is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    if (Find(inner) is { } found)
                    {
                        return found;
                    }
                }

                return null;
            }

            error = error.InnerException;
        }

        return null;
    }

    /// <summary>
    /// Creates the exception the first failure throws.
    /// </summary>
    /// <param name="what">What failed, for the message (for example "a durable flush of the journal").</param>
    /// <param name="cause">The failure.</param>
    internal static StorageOfflineException Create(string what, Exception cause)
        => new(
            $"{ErrorCode}: The storage is offline: {what} failed ({cause.Message}). Records written since the last successful " +
            "durable flush may or may not have reached stable storage, and a retry could report success for writes the " +
            "operating system already dropped, so nothing more is written to the journal or the data file. Reopen the " +
            "storage: its recovery reads the journal and decides the outcome of every commit that was not confirmed.",
            cause);

    /// <summary>
    /// Creates the refusal of a later operation, carrying the failure that took the storage offline.
    /// </summary>
    /// <param name="offline">The exception the first failure threw.</param>
    internal static StorageOfflineException Refusal(StorageOfflineException offline)
        => new(offline.Message, offline.InnerException!);

    /// <summary>
    /// Creates the exception a storage commit throws when its commit record was appended and its
    /// durable flush then failed or was refused: <see cref="CommitRecordWritten"/> is set.
    /// </summary>
    /// <param name="offline">The exception the flush threw.</param>
    internal static StorageOfflineException CommitUnconfirmed(StorageOfflineException offline)
        => new(offline.Message, offline.InnerException!) { CommitRecordWritten = true };
}
