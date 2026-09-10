using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// Extends a control-plane client with the calling application's bearer credential and trust set.
/// </summary>
public interface IAuthenticatedControlPlaneClient : IControlPlaneClient
{
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
