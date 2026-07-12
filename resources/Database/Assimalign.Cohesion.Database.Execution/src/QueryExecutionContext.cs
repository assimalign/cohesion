using System;
using System.Collections.Generic;
using System.Threading;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Execution;

/// <summary>
/// The per-execution state carried through the query pipeline: the request, the
/// transaction scope it runs within, the request-abort token, accumulated
/// diagnostics, and an item bag for engine-specific state between stages.
/// </summary>
/// <remarks>
/// A context lives for exactly one execution. Model-specific planners and operators
/// receive it so every read resolves through the same transaction scope and every
/// diagnostic lands in one place. The item bag is the extension seam for engine
/// state (plan caches, statistics, tracing) — stages must not require entries that
/// earlier stages did not put there.
/// </remarks>
public sealed class QueryExecutionContext
{
    private readonly List<Diagnostic> _diagnostics = new();
    private Dictionary<object, object?>? _items;

    /// <summary>
    /// Initializes a new execution context.
    /// </summary>
    /// <param name="request">The query request being executed.</param>
    /// <param name="transaction">The transaction scope the execution runs within.</param>
    /// <param name="requestAborted">The token that aborts the whole request.</param>
    public QueryExecutionContext(QueryRequest request, IQueryTransactionScope transaction, CancellationToken requestAborted = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(transaction);

        Request = request;
        Transaction = transaction;
        RequestAborted = requestAborted;
    }

    /// <summary>
    /// Gets the query request being executed.
    /// </summary>
    public QueryRequest Request { get; }

    /// <summary>
    /// Gets the transaction scope the execution runs within.
    /// </summary>
    public IQueryTransactionScope Transaction { get; }

    /// <summary>
    /// Gets the token that aborts the whole request. The pipeline observes it
    /// between stages; operators should observe it inside long-running work.
    /// </summary>
    public CancellationToken RequestAborted { get; }

    /// <summary>
    /// Gets the diagnostics accumulated during execution.
    /// </summary>
    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

    /// <summary>
    /// Gets the item bag for engine-specific state shared between pipeline stages.
    /// </summary>
    public IDictionary<object, object?> Items => _items ??= new Dictionary<object, object?>();

    /// <summary>
    /// Adds a diagnostic produced during execution.
    /// </summary>
    /// <param name="diagnostic">The diagnostic to record.</param>
    public void AddDiagnostic(Diagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        lock (_diagnostics)
        {
            _diagnostics.Add(diagnostic);
        }
    }
}
