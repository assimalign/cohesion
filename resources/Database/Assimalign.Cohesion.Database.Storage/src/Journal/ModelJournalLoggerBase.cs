using System;

namespace Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Base helper for model-specific journal adapters (Sql, Document, Graph, Blob, etc.).
/// </summary>
/// <remarks>
/// Derived model components can inherit this type to avoid repeatedly passing model
/// and resource metadata for each transaction.
/// </remarks>
public abstract class ModelJournalLoggerBase
{
    private readonly IJournalLogger _journal;

    /// <summary>
    /// Initializes a model-specific journal adapter.
    /// </summary>
    /// <param name="journal">Underlying journal logger.</param>
    protected ModelJournalLoggerBase(IJournalLogger journal)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
    }

    /// <summary>
    /// Gets the model name used in journal records.
    /// </summary>
    protected abstract string ModelName { get; }

    /// <summary>
    /// Gets the resource name used in journal records.
    /// </summary>
    protected abstract string ResourceName { get; }

    /// <summary>
    /// Begins a transaction for the configured model/resource.
    /// </summary>
    /// <returns>Transaction identifier.</returns>
    protected JournalTransactionId BeginTransaction()
    {
        return _journal.BeginTransaction(ModelName, ResourceName);
    }

    /// <summary>
    /// Appends an operation for a transaction.
    /// </summary>
    /// <param name="transactionId">Transaction identifier.</param>
    /// <param name="operationName">Logical operation name.</param>
    /// <param name="payload">Operation payload.</param>
    /// <returns>Assigned LSN.</returns>
    protected long AppendOperation(JournalTransactionId transactionId, string operationName, ReadOnlySpan<byte> payload)
    {
        return _journal.AppendOperation(transactionId, operationName, payload);
    }

    /// <summary>
    /// Commits a transaction and forces durable flush.
    /// </summary>
    /// <param name="transactionId">Transaction identifier.</param>
    protected void CommitTransaction(JournalTransactionId transactionId)
    {
        _journal.CommitTransaction(transactionId);
    }

    /// <summary>
    /// Rolls back a transaction.
    /// </summary>
    /// <param name="transactionId">Transaction identifier.</param>
    protected void RollbackTransaction(JournalTransactionId transactionId)
    {
        _journal.RollbackTransaction(transactionId);
    }
}
