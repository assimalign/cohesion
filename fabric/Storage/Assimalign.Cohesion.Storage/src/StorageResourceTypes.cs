using Assimalign.Cohesion.Fabric;

namespace Assimalign.Cohesion.Storage;

/// <summary>
/// The resource types the Storage area serves.
/// </summary>
public static class StorageResourceTypes
{
    /// <summary>
    /// <c>Cohesion.Storage/volumes</c>: storage that outlives the workloads that mount it.
    /// </summary>
    public static ResourceTypeName Volumes { get; } = new("Cohesion.Storage/volumes");
}
