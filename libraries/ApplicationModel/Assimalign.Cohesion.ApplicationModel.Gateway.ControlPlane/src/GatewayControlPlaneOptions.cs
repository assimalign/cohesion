using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane;

/// <summary>
/// Configures the hosting-free gateway discovery and command control plane.
/// </summary>
public sealed class GatewayControlPlaneOptions
{
    /// <summary>
    /// Gets or sets the directory beneath which Local gateway discovery metadata is written.
    /// When omitted, no <c>control-plane.json</c> file is published.
    /// </summary>
    public string? MetadataDirectory { get; set; }

    /// <summary>Gets or sets the time source used when validating bearer credentials.</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>
    /// Gets the area command dispatchers, resolved by exact resource kind in registration order.
    /// </summary>
    public IList<IResourceCommandDispatcher> CommandDispatchers { get; } =
        new List<IResourceCommandDispatcher>();
}
