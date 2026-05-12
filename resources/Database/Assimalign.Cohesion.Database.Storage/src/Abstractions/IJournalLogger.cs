using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Defines a journal contract used to guarantee durable transaction ordering.
/// </summary>
/// <remarks>
/// A correct journal implementation is foundational for ACID behavior:
/// Atomicity through begin/commit/rollback markers,
/// Consistency through ordered records and integrity checks,
/// Isolation through serialized write ordering,
/// Durability through forced flush-on-commit.
/// </remarks>
public interface IJournalLogger : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Begins a transaction scope for a model resource.
    /// </summary>
    /// <param name="modelName">Database model identifier (Sql, Document, Graph, etc.).</param>
    /// <param name="resourceName">Logical resource name (table, collection, graph, etc.).</param>
    /// <returns>Created transaction identifier.</returns>
    JournalTransactionId BeginTransaction(string modelName, string resourceName);

    /// <summary>
    /// Appends a logical operation into an active transaction.
    /// </summary>
    /// <param name="transactionId">Transaction identifier.</param>
    /// <param name="operationName">Logical operation name.</param>
    /// <param name="payload">Operation payload.</param>
    /// <returns>Assigned log sequence number (LSN).</returns>
    long AppendOperation(JournalTransactionId transactionId, string operationName, ReadOnlySpan<byte> payload);

    /// <summary>
    /// Commits an active transaction and forces the journal to durable storage.
    /// </summary>
    /// <param name="transactionId">Transaction identifier.</param>
    void CommitTransaction(JournalTransactionId transactionId);

    /// <summary>
    /// Rolls back an active transaction.
    /// </summary>
    /// <param name="transactionId">Transaction identifier.</param>
    void RollbackTransaction(JournalTransactionId transactionId);

    /// <summary>
    /// Writes a checkpoint marker and forces durable flush.
    /// </summary>
    void Checkpoint();

    /// <summary>
    /// Flushes buffered journal data.
    /// </summary>
    /// <param name="forceDurable">When true, requests durable flush semantics where supported.</param>
    void Flush(bool forceDurable = false);

    /// <summary>
    /// Reads all valid records from the journal.
    /// </summary>
    /// <returns>Complete journal record list in LSN order.</returns>
    IReadOnlyList<JournalRecord> ReadAll();

    /// <summary>
    /// Reads only committed operation records eligible for replay.
    /// </summary>
    /// <returns>Committed operation records in LSN order.</returns>
    IReadOnlyList<JournalRecord> RecoverCommittedOperations();
}
