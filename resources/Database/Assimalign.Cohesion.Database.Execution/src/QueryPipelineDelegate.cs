using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Execution;

/// <summary>
/// The continuation a pipeline stage invokes to pass control to the next stage.
/// </summary>
/// <param name="context">The execution context.</param>
/// <param name="cancellationToken">Cancellation token for the operation.</param>
/// <returns>The query result produced downstream.</returns>
public delegate ValueTask<QueryResult> QueryPipelineDelegate(QueryExecutionContext context, CancellationToken cancellationToken);
