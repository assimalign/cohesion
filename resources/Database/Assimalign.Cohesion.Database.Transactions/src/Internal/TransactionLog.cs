using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Transactions.Internal;

using Assimalign.Cohesion.Database.Storage;

/// <summary>
/// The seam that binds the transaction lifecycle to the write-ahead log.
/// </summary>
/// <remarks>
/// <para>
/// The storage layer owns the physical journal; this contract is how the transaction
/// manager appends logical lifecycle records (begin, commit, abort) and enforces the
/// write-ahead rule: a transaction's commit is acknowledged only after its commit
/// record — and every record it depends on — is durable. Implementations are free to
/// batch flushes (group commit) as long as that ordering holds.
/// </para>
/// <para>
/// Internal: the variants are the in-memory log, the journal-bound log and the coordinator's
/// gated journal log, all in this assembly, plus the fault-injecting doubles of this
/// assembly's own tests. A durable manager is the one <see cref="TransactionCoordinator"/>
/// composes; a standalone <see cref="TransactionManager.Create(ILockManager, IVersionStore, Func{TransactionSequence})"/>
/// runs over the in-memory log.
/// </para>
/// </remarks>
internal abstract class TransactionLog
{
    /// <summary>
    /// Creates an in-memory transaction log for embedded working state and tests.
    /// Appends are trivially durable within the process lifetime.
    /// </summary>
    /// <returns>The transaction log.</returns>
    internal static TransactionLog CreateInMemory() => new InMemoryTransactionLog();

    /// <summary>
    /// Creates a transaction log bound to a storage write-ahead journal: lifecycle
    /// records ride the same journal as page images, and commits acknowledge only
    /// after the journal is durable up to the commit record.
    /// </summary>
    /// <param name="journal">The storage journal.</param>
    /// <returns>The transaction log.</returns>
    internal static TransactionLog CreateJournalBound(StorageJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        return new JournalTransactionLog(journal);
    }

    /// <summary>
    /// Appends a begin record for the specified transaction.
    /// </summary>
    /// <param name="sequence">The transaction's sequence.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    public abstract ValueTask AppendBeginAsync(TransactionSequence sequence, CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends a commit record for the specified transaction and returns once the
    /// record is durable per the engine's durability policy.
    /// </summary>
    /// <param name="sequence">The transaction's sequence.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <exception cref="TransactionCommitUnconfirmedException">
    /// The record was appended but could not be made durable. The transaction manager then
    /// ends the transaction as committed, because a written commit record cannot be taken
    /// back. Any other exception means the record was not appended, and the manager aborts
    /// the transaction.
    /// </exception>
    public abstract ValueTask AppendCommitAsync(TransactionSequence sequence, CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends an abort record for the specified transaction.
    /// </summary>
    /// <param name="sequence">The transaction's sequence.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    public abstract ValueTask AppendAbortAsync(TransactionSequence sequence, CancellationToken cancellationToken = default);
}
