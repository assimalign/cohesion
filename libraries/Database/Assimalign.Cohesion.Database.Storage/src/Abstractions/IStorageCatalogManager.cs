using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Manages metadata objects (schemas, collections, labels, indexes, and constraints).
/// </summary>
public interface IStorageCatalogManager
{
    /// <summary>
    /// Creates or updates a catalog entry.
    /// </summary>
    /// <param name="key">Metadata key.</param>
    /// <param name="value">Metadata payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask UpsertAsync(string key, string value, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a catalog entry.
    /// </summary>
    /// <param name="key">Metadata key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Metadata payload or null when missing.</returns>
    ValueTask<string?> ReadAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Enumerates catalog entries by prefix.
    /// </summary>
    /// <param name="prefix">Metadata key prefix.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Matching key/value pairs.</returns>
    IAsyncEnumerable<KeyValuePair<string, string>> ScanAsync(string prefix, CancellationToken cancellationToken = default);
}
