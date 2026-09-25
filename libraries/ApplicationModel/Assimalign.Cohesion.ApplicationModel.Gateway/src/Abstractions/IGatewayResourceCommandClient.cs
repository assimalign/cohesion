using System;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;

using ResourceCommand = Assimalign.Cohesion.Hosting.Resources.ResourceCommand;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>Delivers declarative commands through one area's hosting-free protocol client.</summary>
public interface IGatewayResourceCommandClient
{
    /// <summary>Gets the exact manifest resource kind served by this client.</summary>
    string ResourceKind { get; }

    /// <summary>Applies one owner-scoped declaration.</summary>
    /// <param name="address">The default control-plane endpoint including its manifest path.</param>
    /// <param name="bearerToken">The resource-scoped credential.</param>
    /// <param name="command">The desired command envelope.</param>
    /// <param name="serverCertificateValidator">Validates the target's TLS certificate against the application's transport anchors — the same validator the gateway's probes and store reads use; <see langword="null"/> keeps the platform's default trust (an http target, or an application that has issued no certificate yet).</param>
    /// <param name="cancellationToken">Cancels delivery.</param>
    /// <returns>The observed outcome with the provider's detail.</returns>
    ValueTask<ResourceCommandResult> ApplyAsync(
        Uri address, string bearerToken, ResourceCommand command,
        RemoteCertificateValidationCallback? serverCertificateValidator,
        CancellationToken cancellationToken = default);

    /// <summary>Removes an owned declaration during teardown.</summary>
    /// <param name="address">The default control-plane endpoint including its manifest path.</param>
    /// <param name="bearerToken">The resource-scoped credential.</param>
    /// <param name="command">The previously declared command.</param>
    /// <param name="serverCertificateValidator">Validates the target's TLS certificate against the application's transport anchors — the same validator the gateway's probes and store reads use; <see langword="null"/> keeps the platform's default trust (an http target, or an application that has issued no certificate yet).</param>
    /// <param name="cancellationToken">Cancels delivery.</param>
    /// <returns>The observed deletion outcome.</returns>
    ValueTask<ResourceCommandResult> DeleteAsync(
        Uri address, string bearerToken, ResourceCommand command,
        RemoteCertificateValidationCallback? serverCertificateValidator,
        CancellationToken cancellationToken = default);
}
