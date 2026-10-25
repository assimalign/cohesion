using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>Exposes gateway-owned state management independently of resource runtime assemblies.</summary>
/// <remarks>The serving adapter must authorize each resource, state and actual read/write privilege.</remarks>
public interface IResourceManagementGateway
{
    /// <summary>Gets the resource's authenticated state catalog discovered at startup.</summary>
    /// <param name="application">The application identity.</param>
    /// <param name="resource">The resource identity.</param>
    /// <returns>The runtime manifest JSON.</returns>
    JsonElement GetRuntimeManifest(ApplicationName application, ResourceName resource);
    /// <summary>Reads the current platform-owned membership.</summary>
    /// <param name="application">The application identity.</param>
    /// <param name="resource">The resource identity.</param>
    /// <param name="cancellationToken">Signals cancellation.</param>
    /// <returns>The instance inventory.</returns>
    ValueTask<ResourceInstanceInventory> GetInstancesAsync(ApplicationName application, ResourceName resource, CancellationToken cancellationToken = default);
    /// <summary>Conditionally commits a logical desired value before returning an acceptance receipt.</summary>
    /// <param name="change">The conditional desired-state change.</param>
    /// <param name="verifiedSubject">The serving adapter's authenticated principal identity.</param>
    /// <param name="cancellationToken">Signals cancellation before persistence.</param>
    /// <returns>The durable operation receipt.</returns>
    ValueTask<ResourceStateOperation> SetStateAsync(ResourceStateChange change, string verifiedSubject, CancellationToken cancellationToken = default);
    /// <summary>Reads the current desired revision and convergence for a state slot.</summary>
    /// <param name="address">The state address.</param>
    /// <param name="cancellationToken">Signals cancellation.</param>
    /// <returns>The current operation, or null when unmanaged.</returns>
    ValueTask<ResourceStateOperation?> GetStateAsync(ResourceStateAddress address, CancellationToken cancellationToken = default);
    /// <summary>Reads a durable operation scoped to a resource.</summary>
    /// <param name="application">The application identity.</param>
    /// <param name="resource">The resource identity.</param>
    /// <param name="operationId">The operation identity.</param>
    /// <param name="cancellationToken">Signals cancellation.</param>
    /// <returns>The recorded operation, or null when absent.</returns>
    ValueTask<ResourceStateOperation?> GetStateOperationAsync(ApplicationName application, ResourceName resource, Guid operationId, CancellationToken cancellationToken = default);
}
