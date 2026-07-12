using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Execution;

/// <summary>
/// A composable stage in the query pipeline. Stages run in registration order and
/// may observe, short-circuit, or wrap downstream execution (retry, tracing,
/// timeout enforcement, plan caching).
/// </summary>
public interface IQueryPipelineStage
{
    /// <summary>
    /// Executes this stage.
    /// </summary>
    /// <param name="context">The execution context.</param>
    /// <param name="next">The continuation invoking the rest of the pipeline.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The query result.</returns>
    ValueTask<QueryResult> ExecuteAsync(QueryExecutionContext context, QueryPipelineDelegate next, CancellationToken cancellationToken = default);
}
