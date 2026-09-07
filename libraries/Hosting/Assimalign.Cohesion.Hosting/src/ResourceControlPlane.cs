using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Hosting;

/// <summary>
/// Creates the Core-only control-plane implementation configured by a resource area.
/// </summary>
public static class ResourceControlPlane
{
    /// <summary>
    /// Creates a control plane with the supplied area-defined command kinds.
    /// </summary>
    /// <param name="acceptedCommandKinds">The command kinds accepted by the area.</param>
    /// <returns>A new isolated resource control plane.</returns>
    /// <exception cref="ArgumentException">An accepted command kind is empty.</exception>
    public static IResourceControlPlane Create(IEnumerable<string>? acceptedCommandKinds = null)
    {
        return new DefaultResourceControlPlane(acceptedCommandKinds ?? Array.Empty<string>());
    }
}
