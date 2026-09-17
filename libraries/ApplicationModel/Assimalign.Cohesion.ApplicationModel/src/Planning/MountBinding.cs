namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Binds one manifest mount to a path on the plan's container.
/// </summary>
/// <param name="Mount">The manifest mount name.</param>
/// <param name="ContainerPath">The path at which the materialized data is mounted.</param>
/// <param name="Kind">The kind of mounted data.</param>
/// <param name="Source">The manifest source expression, when one was declared.</param>
public sealed record MountBinding(
    string Mount,
    string ContainerPath,
    ResourceMountKind Kind,
    string? Source);
