using Assimalign.Cohesion.Fabric;

namespace Assimalign.Cohesion.Workloads;

/// <summary>
/// The resource types the Workloads area serves.
/// </summary>
public static class WorkloadResourceTypes
{
    /// <summary>
    /// <c>Cohesion.Workloads/workloads</c>: something to run.
    /// </summary>
    public static ResourceTypeName Workloads { get; } = new("Cohesion.Workloads/workloads");
}
