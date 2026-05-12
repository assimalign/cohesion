using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Execution;

/// <summary>
/// Executes query requests against a database engine.
/// </summary>
/// <remarks>
/// This is an internal infrastructure contract used by session implementations
/// to delegate query execution. It is not exposed on the public engine API.
/// </remarks>
public interface IQueryExecutor
{
    /// <summary>
    /// Executes the specified query request.
    /// </summary>
    /// <param name="request">The query request to execute.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The query result.</returns>
    Task<QueryResult> ExecuteAsync(QueryRequest request, CancellationToken cancellationToken = default);
}
