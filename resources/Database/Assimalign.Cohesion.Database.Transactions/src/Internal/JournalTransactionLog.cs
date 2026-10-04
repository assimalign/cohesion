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
            throw new TransactionCommitUnconfirmedException(
                $"Transaction {sequence} committed, but its commit record could not be made durable; " +
                "the commit is lost if the process stops before the journal is next flushed.", exception);
        }

        return default;
    }

    /// <inheritdoc />
    public ValueTask AppendAbortAsync(TransactionSequence sequence, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _journal.AppendRollback((long)sequence.Value);
        return default;
    }
}
