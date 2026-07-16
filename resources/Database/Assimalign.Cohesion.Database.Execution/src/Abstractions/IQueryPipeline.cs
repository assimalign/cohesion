using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Execution;

/// <summary>
/// A composed query execution flow: ordered stages around a terminal executor,
/// with the transaction boundary semantics of the execution substrate enforced at
/// the edge.
/// </summary>
/// <remarks>
/// The pipeline guarantees, for an <b>implicit</b> (auto-commit) transaction scope:
/// commit after a successful result, rollback after a failed result, an exception,
/// or cancellation — and the original exception always propagates after the
/// rollback. <b>Explicit</b> scopes are never completed by the pipeline. See
/// <see cref="QueryPipelineBuilder"/> to compose one.
/// </remarks>
public interface IQueryPipeline
{
    /// <summary>
    /// Executes a query through the pipeline.
    /// </summary>
    /// <param name="context">The execution context.</param>
    /// <param name="cancellationToken">Cancellation token for the operation, observed together with the context's request-abort token.</param>
    /// <returns>The query result.</returns>
    ValueTask<QueryResult> ExecuteAsync(QueryExecutionContext context, CancellationToken cancellationToken = default);
}
