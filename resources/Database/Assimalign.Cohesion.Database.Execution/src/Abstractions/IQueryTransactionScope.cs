using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Execution;

/// <summary>
/// The transaction boundary a query executes within, as the execution pipeline sees
/// it. Engines implement this over their transaction manager; the pipeline drives
/// the boundary semantics (#164): an <i>implicit</i> scope (auto-commit statement)
/// is committed by the pipeline when execution succeeds and rolled back when it
/// fails or is cancelled; an <i>explicit</i> scope (user transaction) is never
/// completed by the pipeline — the session owns its boundary.
/// </summary>
public interface IQueryTransactionScope : IAsyncDisposable
{
    /// <summary>
    /// Gets a value indicating whether this scope is implicit (statement-scoped
    /// auto-commit) rather than an explicit user transaction.
    /// </summary>
    bool IsImplicit { get; }

    /// <summary>
    /// Gets the current status of the scope.
    /// </summary>
    QueryTransactionStatus Status { get; }

    /// <summary>
    /// Commits the scope. Returns only after the commit is durable per the engine's
    /// durability policy.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    ValueTask CommitAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Rolls the scope back, undoing its effects.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    ValueTask RollbackAsync(CancellationToken cancellationToken = default);
}
