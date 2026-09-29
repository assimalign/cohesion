using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// Serves one application's gateway-owned discovery and command control plane.
/// </summary>
public interface IApplicationGatewayControlPlane
{
    /// <summary>Gets the bound endpoint after the control plane starts.</summary>
    Uri Address { get; }

    /// <summary>Starts serving one application on the requested endpoint.</summary>
    /// <param name="address">The endpoint to bind. A port of zero requests an ephemeral port.</param>
    /// <param name="model">The application model exposed by the control plane.</param>
    /// <param name="state">The application's observed-state view.</param>
    /// <param name="trustedIssuers">The application's trusted issuer provider.</param>
    /// <param name="cancellationToken">Signals that startup should be abandoned.</param>
    /// <returns>A task that completes once the control plane is accepting requests.</returns>
    Task StartAsync(
        Uri address,
        IApplicationModel model,
        IApplicationResourceStateManager state,
        ITrustedIssuerProvider trustedIssuers,
        CancellationToken cancellationToken = default);

    /// <summary>Publishes the latest application export served by this control plane.</summary>
    /// <param name="document">The validated application export.</param>
    /// <param name="cancellationToken">Signals that publication should be abandoned.</param>
    /// <returns>A task that completes after the export is visible to readers.</returns>
    Task PublishAsync(
        ApplicationExportDocument document,
        CancellationToken cancellationToken = default);

    /// <summary>Stops serving and withdraws runtime-scoped discovery metadata.</summary>
    /// <param name="cancellationToken">Bounds shutdown.</param>
    /// <returns>A task that completes after the listener and metadata are gone.</returns>
    Task StopAsync(CancellationToken cancellationToken = default);
}
