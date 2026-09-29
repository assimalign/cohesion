using System;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;

using ResourceCommand = Assimalign.Cohesion.Hosting.Resources.ResourceCommand;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>Delivers declarative commands to a realized resource's control plane without referencing its Hosting package.</summary>
/// <remarks>
/// The gateway selects a client per delivery from <see cref="ApplicationGatewayOptions.CommandClients"/>:
/// the first client whose <see cref="ResourceKind"/> equals the target's manifest kind (ordinal) wins;
/// only when none matches does it use the first client declaring <see cref="AnyKind"/>. The default
/// registration is a single <see cref="ResourceControlPlaneCommandClient"/>, so an exact-kind client
/// added beside it overrides delivery for that kind only.
/// </remarks>
public interface IGatewayResourceCommandClient
{
    /// <summary>
    /// The <see cref="ResourceKind"/> value of a client that serves every manifest kind. Such a client
    /// is the fallback consulted only after no exact-kind client matched; the value is a sentinel,
    /// not a wildcard pattern.
    /// </summary>
    const string AnyKind = "*";

    /// <summary>
    /// Gets the exact manifest resource kind served by this client, or <see cref="AnyKind"/> when the
    /// client serves every kind that has no exact-kind registration.
    /// </summary>
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
