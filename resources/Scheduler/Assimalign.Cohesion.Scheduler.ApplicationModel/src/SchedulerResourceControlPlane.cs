using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.Scheduler.ApplicationModel;

/// <summary>Creates the default control plane for enabled Scheduler resources.</summary>
public static class SchedulerResourceControlPlane
{
    /// <summary>Creates a new isolated Scheduler resource control plane.</summary>
    /// <returns>The Scheduler area's default resource control plane.</returns>
    public static IResourceControlPlane Create() => ResourceControlPlane.Create();
}
