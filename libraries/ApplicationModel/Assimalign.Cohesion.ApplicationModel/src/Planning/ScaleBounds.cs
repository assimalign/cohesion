using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// The platform-neutral range a platform that autoscales may keep a workload's replica count in.
/// </summary>
/// <param name="Minimum">The fewest replicas, at least one.</param>
/// <param name="Maximum">The most replicas, at least <paramref name="Minimum"/>.</param>
/// <remarks>
/// Bounds describe autoscaling, not a fixed count: <see cref="WorkloadSpec.Replicas"/> stays the count a platform
/// runs when it does not autoscale. A Kubernetes gateway realizes the bounds as a horizontal autoscaler; a platform
/// that runs one container per resource refuses a minimum above one. Declared through
/// <see cref="ResourceConfigurationExtensions"/>, validated at <see cref="IApplicationBuilder.Build"/>.
/// </remarks>
[method: JsonConstructor]
public sealed record ScaleBounds(int Minimum, int Maximum);
