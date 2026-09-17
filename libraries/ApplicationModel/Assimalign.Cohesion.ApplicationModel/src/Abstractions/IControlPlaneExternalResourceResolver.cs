using System;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>Exposes the peer gateway address used for external discovery and command delivery.</summary>
public interface IControlPlaneExternalResourceResolver : IExternalResourceResolver
{
    /// <summary>
    /// Gets the peer gateway's control-plane address, or null when the effective resolver
    /// has no peer gateway binding. This is never the target resource's direct endpoint.
    /// </summary>
    Uri? ControlPlaneAddress { get; }
}
