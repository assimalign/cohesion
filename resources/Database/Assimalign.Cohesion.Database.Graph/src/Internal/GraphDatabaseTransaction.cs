using System;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>
/// An explicit graph transaction. A statement that fails inside it aborts the whole transaction
/// (#1188): the engine rolls its work back at once, and the transaction stays the session's
/// transaction, reporting <see cref="TransactionState.Faulted"/>, until the caller ends it.
/// Until then the session refuses every statement with <c>COHDBG007</c>; a rollback ends it, and
/// a commit ends it with <c>COHDBG007</c> and commits nothing. Graph DESIGN.md, "Failed statements
/// in explicit transactions", records the contract and the reference engines it follows.
/// </summary>
internal sealed class GraphDatabaseTransaction : IDatabaseTransaction
{
    private readonly TransactionCoordinator _coordinator;
    private readonly ITransactionContext _context;
    private readonly object _sync = new();
    private Exception? _failure;
    private bool _ended;

    /// <summary>Initializes a new instance of the <see cref="GraphDatabaseTransaction"/> class.</summary>
    /// <param name="coordinator">The transaction coordinator that commits or rolls back the transaction.</param>
    /// <param name="context">The transaction context the transaction wraps.</param>
    public GraphDatabaseTransaction(TransactionCoordinator coordinator, ITransactionContext context)
    {
        _coordinator = coordinator;
        _context = context;
    }

    internal ITransactionContext Context => _context;
    internal int Operations { get; set; }

    /// <summary>
    /// Gets whether the transaction is still the session's transaction: the caller has not ended
    /// it, or its rollback has not completed.
    /// </summary>
    internal bool IsOpen
    {
        get
        {
            lock (_sync)
            {
                return !_ended || _context.State == TransactionState.Active;
            }
        }
    }

    /// <summary>Gets whether statements may run in the transaction: it is open, active and not aborted.</summary>
    internal bool IsUsable
    {
        get
        {
            lock (_sync)
            {
                return !_ended && _failure is null && _context.State == TransactionState.Active;
            }
        }
    }

    /// <summary>Gets the failure that aborted the transaction, or null when no statement failed in it.</summary>
    internal Exception? Failure
    {
        get
        {
            lock (_sync)
            {
                return _failure;
            }
        }
    }

    public TransactionId Id => _context.Id;

    public TransactionState State
    {
        get
        {
            lock (_sync)
            {
                var state = _context.State;
                // A transaction the engine ended under the caller is Faulted until the caller ends it.
                return !_ended && (_failure is not null || state != TransactionState.Active) ? TransactionState.Faulted : state;
            }
        }
    }

    public IsolationLevel IsolationLevel => _context.IsolationLevel;

    public async ValueTask CommitAsync(CancellationToken cancellationToken = default)
    {
        Exception? failure;
        bool aborted;
        lock (_sync)
        {
            var state = _context.State;
            failure = _failure;
            aborted = failure is not null || !_ended && state != TransactionState.Active;
            if (!aborted)
            {
                if (_ended || state != TransactionState.Active)
                {
                    throw new DatabaseException(state == TransactionState.Active
                        ? "The transaction's rollback has not completed."
                        : $"The transaction is {state}.");
                }
                if (Operations != 0)
                {
                    throw new DatabaseException("Dispose every graph operation before committing its transaction.");
                }
            }
            // COMMIT ends the transaction whatever its outcome, as a failed transaction block's
            // COMMIT does in PostgreSQL and a terminated transaction's commit does in Neo4j.
            _ended = true;
        }
        if (!aborted)
        {
            await _coordinator.CommitAsync(_context, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (_context.State == TransactionState.Active)
        {
            // Only when the rollback at the failure did not complete: nothing may commit.
            await _coordinator.RollbackAsync(_context, cancellationToken).ConfigureAwait(false);
        }
        throw CreateAbortedException(failure, commit: true);
    }

    public async ValueTask RollbackAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            var state = _context.State;
            // An aborted transaction accepts any number of rollbacks: its work is already undone,
            // and a caller's catch-block rollback after a failed commit must not raise a second error.
            bool aborted = _failure is not null || !_ended && state != TransactionState.Active;
            if (!aborted && state != TransactionState.Active)
            {
                throw new DatabaseException($"The transaction is {state}.");
            }
            _ended = true;
        }
        if (_context.State == TransactionState.Active)
        {
            await _coordinator.RollbackAsync(_context, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            _ended = true;
        }
        if (_context.State == TransactionState.Active)
        {
            await _coordinator.RollbackAsync(_context).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Aborts the transaction because a statement failed in it: records the first failure, then
    /// rolls the transaction's work back so it holds no writer lock while it waits for the
    /// caller's rollback. The failure is recorded first, so the session refuses later statements
    /// even when the rollback itself fails.
    /// </summary>
    /// <param name="cause">The failure the caller observed from the statement.</param>
    internal async ValueTask AbortAsync(Exception cause)
    {
        lock (_sync)
        {
            if (!_ended)
            {
                _failure ??= cause;
            }
        }
        if (_context.State == TransactionState.Active)
        {
            await _coordinator.RollbackAsync(_context).ConfigureAwait(false);
        }
    }

    /// <summary>Creates the <c>COHDBG007</c> error for work refused by an aborted transaction.</summary>
    /// <param name="failure">The failure that aborted the transaction, or null when it ended for another reason.</param>
    /// <param name="commit">True when the refused work is the transaction's commit.</param>
    /// <returns>The coded error, carrying <paramref name="failure"/> as its inner exception.</returns>
    internal static DatabaseException CreateAbortedException(Exception? failure, bool commit = false)
    {
        // The cause travels in the message as well: the wire carries message text, not inner exceptions.
        string cause = failure is null ? string.Empty : " Cause: " + failure.Message;
        string message = commit
            ? "COHDBG007: The session's transaction is aborted and cannot commit; nothing was committed." + cause
            : "COHDBG007: The session's transaction is aborted; statements are refused until it is rolled back." + cause;
        return new DatabaseException(message, failure);
    }
}
