namespace Assimalign.Cohesion.Storage;

/// <summary>
/// How workloads may mount a volume.
/// </summary>
public enum VolumeAccessMode
{
    /// <summary>
    /// One instance mounts it for reading and writing.
    /// </summary>
    ReadWriteOnce,

    /// <summary>
    /// Many instances mount it for reading.
    /// </summary>
    ReadOnlyMany,

    /// <summary>
    /// Many instances mount it for reading and writing.
    /// </summary>
    ReadWriteMany,
}
