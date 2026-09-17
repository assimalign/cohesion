using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Reads the application document exposed by another gateway's control plane.
/// </summary>
/// <remarks>
/// The HTTP transport is supplied by the control-plane package. The base application model
/// depends only on this typed seam so file and test implementations remain NativeAOT-safe.
/// </remarks>
public interface IControlPlaneClient
{
    /// <summary>Gets the exported application at a gateway control-plane address.</summary>
    /// <param name="address">The peer gateway control-plane address.</param>
    /// <param name="cancellationToken">Signals that resolution should be abandoned.</param>
    /// <returns>The peer's current application export.</returns>
    ValueTask<ApplicationExportDocument> GetApplicationAsync(
        Uri address,
        CancellationToken cancellationToken = default);
}
