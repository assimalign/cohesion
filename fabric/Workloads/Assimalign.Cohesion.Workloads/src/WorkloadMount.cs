using Assimalign.Cohesion.Fabric;

namespace Assimalign.Cohesion.Workloads;

/// <summary>
/// A volume a workload mounts. The volume is a Storage-area resource the workload reaches through
/// a <c>$ref</c>, never by a direct type reference, so the two areas stay independent.
/// </summary>
/// <param name="Volume">The volume.</param>
/// <param name="Path">Where the volume is mounted inside the workload.</param>
public sealed record WorkloadMount(FabricRef Volume, string Path);
