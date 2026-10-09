using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.KeyValuePair.Internal;

/// <summary>
/// The per-command execution context: the MVCC transaction the command runs
/// under, the visibility snapshot captured once at command start, and the
/// database's transaction coordinator (the key lock in phase one, the gated
/// apply bracket in phase two). Under <c>IsolationLevel.ReadCommitted</c> the
/// transaction's own context re-captures its snapshot per access, so the session
/// hands this context a statement view instead
/// (<c>TransactionContext.PinStatementSnapshot</c>), pinned behind a snapshot
/// transaction it ends with the command: the view gives the command exactly one
/// refreshed view, and the pin keeps the version purge from reclaiming what that view
/// still sees (#1363). Under <c>Snapshot</c> isolation the same capture returns the
/// begin-time snapshot for every command, which the transaction itself pins.
/// </summary>
internal readonly struct KeyValueStatementContext
{
    internal KeyValueStatementContext(TransactionContext transaction, TransactionCoordinator coordinator)
    {
        Transaction = transaction;
        Coordinator = coordinator;
        Snapshot = transaction.Snapshot;
    }

    /// <summary>
    /// Gets the MVCC transaction context the command executes under.
    /// </summary>
    internal TransactionContext Transaction { get; }

    /// <summary>
    /// Gets the database's transaction coordinator.
    /// </summary>
    internal TransactionCoordinator Coordinator { get; }

    /// <summary>
    /// Gets the visibility snapshot for the whole command.
    /// </summary>
    internal TransactionSnapshot Snapshot { get; }
}
