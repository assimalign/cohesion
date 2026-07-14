using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Sql.Internal;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

/// <summary>
/// Internal ACID transaction implementation that wraps a storage-level transaction:
/// commit is acknowledged only after the write-ahead log is durable, and rollback
/// restores every modified page to its pre-transaction image.
/// </summary>
internal sealed class SqlDatabaseTransaction : IDatabaseTransaction
{
    private TransactionState _state;

    internal SqlDatabaseTransaction(TransactionId id, IStorageTransaction storageTransaction, IsolationLevel isolationLevel = IsolationLevel.Snapshot)
    {
        Id = id;
        StorageTransaction = storageTransaction;
        IsolationLevel = isolationLevel;
        _state = TransactionState.Active;
    }

    /// <inheritdoc />
    public TransactionId Id { get; }

    /// <inheritdoc />
    public TransactionState State => _state;

    /// <inheritdoc />
    /// <remarks>
    /// Carried for the contract; the engine currently executes every level
    /// conservatively — writers serialize at page grain through the storage
    /// transaction, which is stronger than any requested level's write behavior.
    /// Per-level snapshot visibility lands with the MVCC session binding (see the
    /// transaction-integration design in <c>resources/Database/DESIGN.md</c>).
    /// </remarks>
    public IsolationLevel IsolationLevel { get; }

    /// <summary>
    /// Gets the storage-level transaction the executor mutates through.
    /// </summary>
    internal IStorageTransaction StorageTransaction { get; }

    /// <inheritdoc />
    public ValueTask CommitAsync(CancellationToken cancellationToken = default)
    {
        if (_state != TransactionState.Active)
        {
            throw new DatabaseException($"Cannot commit transaction in state '{_state}'.");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Durable by contract: the storage transaction journals after images and
            // returns only once the commit record is on stable storage (no-force —
            // data pages flush lazily; recovery replays them).
            StorageTransaction.Commit();

            _state = TransactionState.Committed;
        }
        catch (OperationCanceledException)
        {
            _state = TransactionState.Faulted;
            throw;
        }
        catch
        {
            _state = TransactionState.Faulted;
            throw;
        }

        return default;
    }

    /// <inheritdoc />
    public ValueTask RollbackAsync(CancellationToken cancellationToken = default)
    {
        if (_state != TransactionState.Active)
        {
            throw new DatabaseException($"Cannot rollback transaction in state '{_state}'.");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            StorageTransaction.Rollback();

            _state = TransactionState.RolledBack;
        }
        catch (OperationCanceledException)
        {
            _state = TransactionState.Faulted;
            throw;
        }
        catch
        {
            _state = TransactionState.Faulted;
            throw;
        }

        return default;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_state == TransactionState.Active)
        {
            await RollbackAsync().ConfigureAwait(false);
        }
    }
}
