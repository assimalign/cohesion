using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Sql.Internal;

using Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Internal ACID transaction implementation that wraps a storage-level transaction:
/// commit is acknowledged only after the write-ahead log is durable, and rollback
/// restores every modified page to its pre-transaction image.
/// </summary>
internal sealed class SqlDatabaseTransaction : IDatabaseTransaction
{
    private TransactionState _state;

    internal SqlDatabaseTransaction(TransactionId id, IStorageTransaction storageTransaction)
    {
        Id = id;
        StorageTransaction = storageTransaction;
        _state = TransactionState.Active;
    }

    /// <inheritdoc />
    public TransactionId Id { get; }

    /// <inheritdoc />
    public TransactionState State => _state;

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
