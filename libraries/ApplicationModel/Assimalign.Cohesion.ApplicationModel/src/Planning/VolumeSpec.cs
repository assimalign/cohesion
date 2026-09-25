namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Describes storage that a platform compiler must materialize for a resource plan.
/// </summary>
/// <param name="Name">The volume name, matching its container mount binding.</param>
/// <param name="Kind">The manifest mount kind materialized by this volume.</param>
/// <param name="Size">The requested storage size.</param>
/// <param name="PerReplicaClaim">
/// Whether the platform must create an independent claim for each workload replica.
/// </param>
public sealed record VolumeSpec(
    string Name,
    ResourceMountKind Kind,
    string Size,
    bool PerReplicaClaim);
