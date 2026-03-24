using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Builds and maintains secondary indexes for model resources.
/// </summary>
public interface IStorageIndexManager
{
    /// <summary>
    /// Creates an index over a logical resource and key specification.
    /// </summary>
    /// <param name="resourceName">Logical resource name.</param>
    /// <param name="indexName">Index name.</param>
    /// <param name="keyExpression">Model-specific key expression.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask CreateIndexAsync(string resourceName, string indexName, string keyExpression, CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops an index.
    /// </summary>
    /// <param name="resourceName">Logical resource name.</param>
    /// <param name="indexName">Index name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask DropIndexAsync(string resourceName, string indexName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists index names for a logical resource.
    /// </summary>
    /// <param name="resourceName">Logical resource name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Index names.</returns>
    ValueTask<IReadOnlyList<string>> ListIndexesAsync(string resourceName, CancellationToken cancellationToken = default);
}
