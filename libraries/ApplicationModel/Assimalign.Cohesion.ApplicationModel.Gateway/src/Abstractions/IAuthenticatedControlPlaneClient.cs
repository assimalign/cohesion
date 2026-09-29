using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using ResourceCommand = Assimalign.Cohesion.Hosting.Resources.ResourceCommand;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// Extends a control-plane client with the calling application's bearer credential and trust set.
/// </summary>
public interface IAuthenticatedControlPlaneClient : IControlPlaneClient
{
    /// <summary>Applies an owned command through the peer gateway's resource control plane.</summary>
    /// <param name="address">The peer gateway address.</param>
    /// <param name="resource">The peer's target resource name.</param>
    /// <param name="bearerToken">The declaring application's signed gateway credential.</param>
    /// <param name="command">The owner-stamped command envelope.</param>
    /// <param name="cancellationToken">Cancels delivery.</param>
    /// <returns>The peer's observed outcome and detail.</returns>
    /// <exception cref="NotSupportedException">The client does not support command delivery.</exception>
    ValueTask<ResourceCommandResult> ApplyCommandAsync(
        Uri address, ResourceName resource, string bearerToken, ResourceCommand command,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This peer client does not support resource commands.");

    /// <summary>Removes an owned command through the peer gateway.</summary>
    /// <param name="address">The peer gateway address.</param>
    /// <param name="resource">The peer's target resource name.</param>
    /// <param name="bearerToken">The declaring application's signed gateway credential.</param>
    /// <param name="command">The previously declared command.</param>
    /// <param name="cancellationToken">Cancels delivery.</param>
    /// <returns>The peer's observed deletion outcome.</returns>
    /// <exception cref="NotSupportedException">The client does not support command delivery.</exception>
    ValueTask<ResourceCommandResult> DeleteCommandAsync(
        Uri address, ResourceName resource, string bearerToken, ResourceCommand command,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This peer client does not support resource commands.");

    /// <summary>Gets a peer export using an application-issued credential and trusted peer keys.</summary>
    /// <param name="address">The peer gateway control-plane address.</param>
    /// <param name="bearerToken">The calling application's bearer token.</param>
    /// <param name="trustedIssuers">The calling application's trusted issuer snapshot.</param>
    /// <param name="cancellationToken">Signals that resolution should be abandoned.</param>
    /// <returns>The verified peer application export.</returns>
    ValueTask<ApplicationExportDocument> GetApplicationAsync(
        Uri address,
        string bearerToken,
        IReadOnlyList<TrustedIssuer> trustedIssuers,
        CancellationToken cancellationToken = default);
}
