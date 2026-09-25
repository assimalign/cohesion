using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Execution;

/// <summary>
/// Represents a query result that returns a set of rows.
/// </summary>
/// <remarks>
/// Rows are streamed via <see cref="GetRowsAsync"/> to support large result sets
/// without requiring full materialization in memory.
/// </remarks>
public abstract class QueryResultSet : QueryResult, IAsyncDisposable
{
    /// <summary>
    /// Gets the column descriptors for the result set.
    /// </summary>
    public abstract IReadOnlyList<QueryColumn> Columns { get; }

    /// <summary>
    /// Streams result rows asynchronously.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>An async sequence of result rows.</returns>
    public abstract IAsyncEnumerable<QueryRow> GetRowsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases resources held by the result set.
    /// </summary>
    public abstract ValueTask DisposeAsync();
}
