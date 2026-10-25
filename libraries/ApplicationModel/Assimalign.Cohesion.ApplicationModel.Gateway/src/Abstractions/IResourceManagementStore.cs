using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>Persists gateway management intent with atomic conditional replacement.</summary>
public interface IResourceManagementStore
{
    /// <summary>Reads the current per-resource management document.</summary>
    /// <param name="application">The application identity.</param>
    /// <param name="resource">The resource identity.</param>
    /// <param name="cancellationToken">Signals cancellation.</param>
    /// <returns>The document, or null when absent.</returns>
    ValueTask<ResourceManagementDocument?> ReadAsync(ApplicationName application, ResourceName resource, CancellationToken cancellationToken = default);
    /// <summary>Atomically replaces a document if its version matches.</summary>
    /// <param name="application">The application identity.</param>
    /// <param name="resource">The resource identity.</param>
    /// <param name="expectedVersion">The read token; null explicitly requires absence.</param>
    /// <param name="content">The complete replacement document.</param>
    /// <param name="cancellationToken">Signals cancellation before the atomic commit.</param>
    /// <returns>Whether the replacement committed.</returns>
    ValueTask<bool> TryWriteAsync(ApplicationName application, ResourceName resource, string? expectedVersion, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default);
}
