using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>Atomically persists intent and a protected platform projection for current and future instances.</summary>
public interface IProjectedResourceManagementStore : IResourceManagementStore
{
    /// <summary>Atomically commits private coordination state and its workload-visible desired-state projection.</summary>
    /// <param name="application">The application identity.</param>
    /// <param name="resource">The resource identity.</param>
    /// <param name="expectedVersion">The expected storage version; null requires absent intent.</param>
    /// <param name="content">The private gateway coordination document.</param>
    /// <param name="projection">The resource-visible state projection, without caller history or credentials.</param>
    /// <param name="cancellationToken">Signals cancellation before commit.</param>
    /// <returns>Whether both documents committed together.</returns>
    ValueTask<bool> TryWriteProjectionAsync(ApplicationName application, ResourceName resource, string? expectedVersion,
        ReadOnlyMemory<byte> content, ReadOnlyMemory<byte> projection, CancellationToken cancellationToken = default);
}
