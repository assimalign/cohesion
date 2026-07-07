using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Blob;

/// <summary>
/// A named container of streamed large objects.
/// </summary>
/// <remarks>
/// Blob content is streamed and never buffered whole; the metadata catalog is
/// transactional. A blob written through <see cref="OpenWriteAsync"/> becomes
/// visible atomically when the returned stream is disposed after a successful
/// write — readers never observe partial content.
/// </remarks>
public interface IBlobContainer
{
    /// <summary>
    /// Gets the name of the container, unique within its database.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Opens a stream that writes a blob's content. The blob becomes visible
    /// atomically when the stream is disposed after a complete write.
    /// </summary>
    /// <param name="name">The blob name, unique within the container.</param>
    /// <param name="options">Write options (content type, overwrite behavior), or null for defaults.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A writable stream for the blob content.</returns>
    /// <exception cref="DatabaseException">Thrown when the blob exists and overwrite is not permitted.</exception>
    ValueTask<Stream> OpenWriteAsync(string name, BlobWriteOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a stream that reads a blob's content.
    /// </summary>
    /// <param name="name">The blob name.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A readable stream over the blob content.</returns>
    /// <exception cref="DatabaseException">Thrown when the blob does not exist.</exception>
    ValueTask<Stream> OpenReadAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a blob's metadata.
    /// </summary>
    /// <param name="name">The blob name.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The blob's properties, or null when the blob does not exist.</returns>
    ValueTask<BlobProperties?> GetPropertiesAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a blob.
    /// </summary>
    /// <param name="name">The blob name.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>True when the blob was deleted; false when it did not exist.</returns>
    ValueTask<bool> DeleteAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams the metadata of blobs in the container, in name order.
    /// </summary>
    /// <param name="prefix">When set, only blobs whose names start with the prefix are returned.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>An async sequence of blob properties.</returns>
    IAsyncEnumerable<BlobProperties> GetBlobsAsync(string? prefix = null, CancellationToken cancellationToken = default);
}
