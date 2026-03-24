using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Sql.Internal;

using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Internal ACID transaction implementation that wraps the journal transaction.
/// </summary>
internal sealed class SqlDatabaseTransaction : IDatabaseTransaction
{
    private readonly IJournalLogger _journal;
    private readonly SqlStorage _storage;

    private TransactionState _state;

    internal SqlDatabaseTransaction(TransactionId id, JournalTransactionId journalTransactionId, IJournalLogger journal, SqlStorage storage)
    {
        Id = id;
        JournalTransactionId = journalTransactionId;
        _journal = journal;
        _storage = storage;
        _state = TransactionState.Active;
    }

    /// <inheritdoc />
    public TransactionId Id { get; }

    /// <inheritdoc />
    public TransactionState State => _state;

    /// <summary>
    /// Gets the journal-level transaction identifier used by the executor for WAL operations.
    /// </summary>
    internal JournalTransactionId JournalTransactionId { get; }

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

            // Force durable flush of the journal first (durability guarantee)
            _journal.CommitTransaction(JournalTransactionId);

            // Then flush dirty data pages to storage
            _storage.FlushChanges();

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

            _journal.RollbackTransaction(JournalTransactionId);

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
