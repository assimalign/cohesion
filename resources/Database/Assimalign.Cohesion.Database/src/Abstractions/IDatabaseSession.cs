using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Transactions;

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
    /// Begins an explicit ACID transaction within this session, at the default
    /// isolation level (<see cref="IsolationLevel.Snapshot"/>).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A new transaction instance.</returns>
    /// <exception cref="DatabaseException">Thrown when a transaction is already active.</exception>
    ValueTask<IDatabaseTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Begins an explicit ACID transaction within this session at the requested
    /// isolation level.
    /// </summary>
    /// <param name="isolationLevel">The isolation level the transaction executes under (the <c>Database.Transactions</c> vocabulary the root consumes through its child-root reference).</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A new transaction instance.</returns>
    /// <exception cref="DatabaseException">Thrown when a transaction is already active, or when the engine does not support <paramref name="isolationLevel"/>.</exception>
    /// <remarks>
    /// The requested level is carried on the returned transaction
    /// (<see cref="IDatabaseTransaction.IsolationLevel"/>). An engine may honor a
    /// request conservatively by running at a <em>stronger</em> level than asked —
    /// the SQL engine currently serializes writers at page grain, for example —
    /// but must never run weaker than requested. Per-level MVCC snapshot
    /// semantics land with the engines' MVCC session binding (the
    /// transaction-integration design in <c>resources/Database/DESIGN.md</c>).
    /// </remarks>
    ValueTask<IDatabaseTransaction> BeginTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a query request against the database.
    /// </summary>
    /// <param name="request">The query request to execute.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The query result.</returns>
    ValueTask<QueryResult> ExecuteAsync(QueryRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes statement text in the session's own language (SQL for a SQL
    /// session, and so on), binding the given named parameter values.
    /// </summary>
    /// <param name="statement">The statement text to parse and execute.</param>
    /// <param name="parameters">Parameter values keyed by bare parameter name, or null when the statement takes none.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The query result.</returns>
    /// <exception cref="DatabaseParseException">Thrown when the statement text fails to parse in the session's language.</exception>
    /// <exception cref="DatabaseException">Thrown when the statement fails during planning or execution.</exception>
    /// <remarks>
    /// This is the model-agnostic text-execute seam consumed by the wire-protocol
    /// server (the server runtime in <c>Database.Hosting</c>): the server receives statement text and
    /// decoded parameters off the wire, and each model's session owns translating
    /// that text into its typed <see cref="QueryRequest"/> — the server never
    /// parses any model language.
    /// </remarks>
    ValueTask<QueryResult> ExecuteAsync(string statement, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default);
}
