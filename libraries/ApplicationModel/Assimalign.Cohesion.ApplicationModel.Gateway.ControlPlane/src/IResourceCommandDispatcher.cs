using System;
using System.Net.Security;
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
    /// <param name="serverCertificateValidator">Validates the target's TLS certificate against the application's transport anchors — the same validator the gateway's probes and store reads use; <see langword="null"/> keeps the platform's default trust (an http target, or an application that has issued no certificate yet).</param>
    /// <param name="cancellationToken">Cancels dispatch.</param>
    /// <returns>The area-defined response payload.</returns>
    ValueTask<ReadOnlyMemory<byte>> ApplyAsync(
        Uri address,
        string bearerToken,
        ResourceCommand command,
        RemoteCertificateValidationCallback? serverCertificateValidator,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes a previously applied command from a resource's default control plane.</summary>
    /// <param name="address">The resource's observed default control-plane endpoint.</param>
    /// <param name="bearerToken">The resource-scoped bootstrap credential.</param>
    /// <param name="command">The previously accepted command envelope.</param>
    /// <param name="serverCertificateValidator">Validates the target's TLS certificate against the application's transport anchors — the same validator the gateway's probes and store reads use; <see langword="null"/> keeps the platform's default trust (an http target, or an application that has issued no certificate yet).</param>
    /// <param name="cancellationToken">Cancels dispatch.</param>
    /// <returns>A task that completes after deletion is applied.</returns>
    ValueTask DeleteAsync(
        Uri address,
        string bearerToken,
        ResourceCommand command,
        RemoteCertificateValidationCallback? serverCertificateValidator,
        CancellationToken cancellationToken = default);
}
