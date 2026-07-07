using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Transactions;

/// <summary>
/// The seam that binds the transaction lifecycle to the write-ahead log.
/// </summary>
/// <remarks>
/// The storage layer owns the physical journal; this contract is how the transaction
/// manager appends logical lifecycle records (begin, commit, abort) and enforces the
/// write-ahead rule: a transaction's commit is acknowledged only after its commit
/// record — and every record it depends on — is durable. Implementations are free to
/// batch flushes (group commit) as long as that ordering holds.
/// </remarks>
public interface ITransactionLog
{
    /// <summary>
    /// Appends a begin record for the specified transaction.
    /// </summary>
    /// <param name="sequence">The transaction's sequence.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    ValueTask AppendBeginAsync(TransactionSequence sequence, CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends a commit record for the specified transaction and returns once the
    /// record is durable per the engine's durability policy.
    /// </summary>
    /// <param name="sequence">The transaction's sequence.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    ValueTask AppendCommitAsync(TransactionSequence sequence, CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends an abort record for the specified transaction.
    /// </summary>
    /// <param name="sequence">The transaction's sequence.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    ValueTask AppendAbortAsync(TransactionSequence sequence, CancellationToken cancellationToken = default);
}
