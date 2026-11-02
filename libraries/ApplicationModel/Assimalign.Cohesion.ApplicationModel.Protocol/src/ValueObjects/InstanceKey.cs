namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Identifies one instance of a resource: one start of one process. Every instance of a resource
/// shares the resource's provider and name, and the key tells them apart.
/// </summary>
/// <remarks>
/// An instance creates its key when it starts, so a restarted process is a new instance with a new
/// key. The key is a ULID, so keys sort by the time the instances started.
/// </remarks>
public readonly partial struct InstanceKey
{
}
