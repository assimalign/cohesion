using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Transactions.Internal;

using Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Transaction log bound to the storage write-ahead log: lifecycle records ride the
/// same journal as page images, and commit acknowledges only after the journal is
/// durable up to the commit record (the write-ahead rule). Group commit falls out of
/// the journal's <see cref="IStorageJournal.EnsureDurable"/> — a flush that covers one
/// commit covers every earlier record, so concurrent commits share fsyncs.
/// </summary>
internal sealed class JournalTransactionLog : ITransactionLog
{
    private readonly IStorageJournal _journal;

    internal JournalTransactionLog(IStorageJournal journal)
    {
        _journal = journal;
    }

    /// <inheritdoc />
    public ValueTask AppendBeginAsync(TransactionSequence sequence, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _journal.AppendBegin((long)sequence.Value);
        return default;
    }

    /// <inheritdoc />
    public ValueTask AppendCommitAsync(TransactionSequence sequence, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long lsn = _journal.AppendCommit((long)sequence.Value);

        try
        {
            _journal.EnsureDurable(lsn);
        }
        catch (Exception exception)
        {
            // The record is in the journal, which recovery reads as committed: the
            // transaction can no longer abort, only its durability is open.
            throw new TransactionCommitUnconfirmedException(UnconfirmedMessage(sequence), exception);
        }

        return default;
    }

    /// <summary>
    /// The message of a commit whose record was appended but whose durable flush failed, which
    /// took the journal's storage offline (#1243).
    /// </summary>
    /// <param name="sequence">The transaction's sequence.</param>
    internal static string UnconfirmedMessage(TransactionSequence sequence)
        => $"Transaction {sequence} committed in this process, but its commit record could not be made durable, so its " +
           "outcome is unknown: the failed flush took the storage offline, and nothing more is written to it. Reopen the " +
           "database; its recovery keeps the commit if the record reached stable storage and discards it if not.";

    /// <inheritdoc />
    public ValueTask AppendAbortAsync(TransactionSequence sequence, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _journal.AppendRollback((long)sequence.Value);
        return default;
    }
}
