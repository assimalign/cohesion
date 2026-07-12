using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Transactions;

using Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Transaction log bound to the storage write-ahead log: lifecycle records ride the
/// same journal as page images, and commit acknowledges only after the journal is
/// durable up to the commit record (the write-ahead rule). Group commit falls out of
/// the journal's <see cref="IJournal.EnsureDurable"/> — a flush that covers one
/// commit covers every earlier record, so concurrent commits share fsyncs.
/// </summary>
internal sealed class JournalTransactionLog : ITransactionLog
{
    private readonly IJournal _journal;

    internal JournalTransactionLog(IJournal journal)
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
        _journal.EnsureDurable(lsn);
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
