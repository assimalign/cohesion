using Assimalign.Cohesion.Fabric;

namespace Assimalign.Cohesion.Networking;

/// <summary>
/// The resource types the Networking area serves.
/// </summary>
public static class NetworkingResourceTypes
{
    /// <summary>
    /// <c>Cohesion.Networking/exposures</c>: who may reach a workload's port.
    /// </summary>
    public static ResourceTypeName Exposures { get; } = new("Cohesion.Networking/exposures");
}
