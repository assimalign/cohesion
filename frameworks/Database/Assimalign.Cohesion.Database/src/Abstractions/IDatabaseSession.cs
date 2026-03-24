using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// Represents a lightweight, scoped execution context within a database.
/// </summary>
/// <remarks>
/// A session serves as the entry point for query execution and transaction management.
/// Sessions are not thread-safe and should not be shared across concurrent operations.
/// Disposing a session rolls back any active transaction.
/// </remarks>
public interface IDatabaseSession : IAsyncDisposable
{
    /// <summary>
    /// Gets the database this session is scoped to.
    /// </summary>
    IDatabase Database { get; }

    /// <summary>
    /// Gets the current lifecycle state of the session.
    /// </summary>
    SessionState State { get; }

    /// <summary>
    /// Gets the currently active transaction, or null if no transaction is active.
    /// </summary>
    IDatabaseTransaction? CurrentTransaction { get; }

    /// <summary>
    /// Begins an explicit ACID transaction within this session.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A new transaction instance.</returns>
    /// <exception cref="DatabaseException">Thrown when a transaction is already active.</exception>
    ValueTask<IDatabaseTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a query request against the database.
    /// </summary>
    /// <param name="request">The query request to execute.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The query result.</returns>
    ValueTask<QueryResult> ExecuteAsync(QueryRequest request, CancellationToken cancellationToken = default);
}
