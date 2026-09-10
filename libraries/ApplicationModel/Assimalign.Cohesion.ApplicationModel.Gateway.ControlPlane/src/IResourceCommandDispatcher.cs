using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane;

/// <summary>
/// Applies gateway resource commands through an area-owned, hosting-free protocol client.
/// </summary>
public interface IResourceCommandDispatcher
{
    /// <summary>Gets the exact resource manifest kind handled by this dispatcher.</summary>
    string ResourceKind { get; }

    /// <summary>Applies a desired command to a resource's default control plane.</summary>
    /// <param name="address">The resource's observed default control-plane endpoint.</param>
    /// <param name="bearerToken">The resource-scoped bootstrap credential.</param>
    /// <param name="command">The command envelope to apply.</param>
    /// <param name="cancellationToken">Cancels dispatch.</param>
    /// <returns>The area-defined response payload.</returns>
    ValueTask<ReadOnlyMemory<byte>> ApplyAsync(
        Uri address,
        string bearerToken,
        ResourceCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes a previously applied command from a resource's default control plane.</summary>
    /// <param name="address">The resource's observed default control-plane endpoint.</param>
    /// <param name="bearerToken">The resource-scoped bootstrap credential.</param>
    /// <param name="command">The previously accepted command envelope.</param>
    /// <param name="cancellationToken">Cancels dispatch.</param>
    /// <returns>A task that completes after deletion is applied.</returns>
    ValueTask DeleteAsync(
        Uri address,
        string bearerToken,
        ResourceCommand command,
        CancellationToken cancellationToken = default);
}
