namespace Assimalign.Cohesion.Storage;

/// <summary>
/// The desired state of a <c>Cohesion.Storage/volumes</c> resource: storage that outlives the
/// workloads that mount it.
/// </summary>
/// <remarks>
/// A workload mounts a volume through a <c>$ref</c>, so the Storage and Workloads areas do not
/// reference each other (rule F4). Init properties, so the spec can grow without breaking callers.
/// </remarks>
public sealed record VolumeSpec
{
    /// <summary>
    /// Gets the capacity the volume requests, in bytes.
    /// </summary>
    public required long CapacityBytes { get; init; }

    /// <summary>
    /// Gets how workloads may mount the volume.
    /// </summary>
    public VolumeAccessMode AccessMode { get; init; } = VolumeAccessMode.ReadWriteOnce;
}
