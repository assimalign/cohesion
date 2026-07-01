namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// A configuration, secret, or volume mount required by a resource.
/// </summary>
/// <param name="Name">A logical name for the mount.</param>
/// <param name="Path">The path the data is mounted at inside the realized resource.</param>
/// <param name="Kind">The kind of data being mounted.</param>
public readonly record struct ResourceMount(
    string Name,
    string Path,
    ResourceMountKind Kind);

/// <summary>
/// The kind of data a <see cref="ResourceMount"/> carries.
/// </summary>
public enum ResourceMountKind
{
    /// <summary>Non-sensitive configuration data.</summary>
    Configuration = 0,

    /// <summary>Sensitive secret data.</summary>
    Secret,

    /// <summary>A persistent or ephemeral volume.</summary>
    Volume
}
