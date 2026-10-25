using System;
using System.Threading;
using Assimalign.Cohesion.Database.Graph.Internal;

namespace Assimalign.Cohesion.Database.Graph;

/// <summary>Borrowed statement context; never retained beyond a callback.</summary>
[System.Diagnostics.DebuggerDisplay("GraphFunctionContext")]
[System.Diagnostics.DebuggerTypeProxy(typeof(GraphFunctionContext.DebugView))]
public readonly ref struct GraphFunctionContext
{
    private readonly GraphEvaluationContext _evaluation;
    /// <summary>Initializes a standalone context.</summary>
    /// <param name="databaseName">The database name.</param><param name="transactionTimestamp">The captured transaction instant.</param>
    /// <param name="cancellationToken">The statement cancellation token.</param>
    public GraphFunctionContext(DatabaseName databaseName, DateTimeOffset transactionTimestamp, CancellationToken cancellationToken = default)
    { DatabaseName = databaseName; TransactionTimestamp = transactionTimestamp; CancellationToken = cancellationToken; _evaluation = new(transactionTimestamp); }
    internal GraphFunctionContext(GraphEvaluationContext evaluation)
    { _evaluation = evaluation; DatabaseName = evaluation.Database?.Name ?? default; TransactionTimestamp = evaluation.TransactionStart; CancellationToken = evaluation.Operation?.FunctionCancellation ?? default; }
    internal GraphEvaluationContext Evaluation => _evaluation;
    /// <summary>Gets the database name, or default for a standalone invocation.</summary>
    public DatabaseName DatabaseName { get; }
    /// <summary>Gets the immutable UTC instant captured when the transaction began.</summary>
    public DateTimeOffset TransactionTimestamp { get; }
    /// <summary>Gets the statement cancellation token.</summary>
    public CancellationToken CancellationToken { get; }
    private sealed class DebugView
    {
        public DebugView(GraphFunctionContext value) { DatabaseName = value.DatabaseName; TransactionTimestamp = value.TransactionTimestamp; }
        public DatabaseName DatabaseName { get; }
        public DateTimeOffset TransactionTimestamp { get; }
    }
}
