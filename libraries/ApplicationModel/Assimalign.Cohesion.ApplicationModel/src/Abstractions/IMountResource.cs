using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// A capability interface for a resource that mounts configuration, secret, or volume data.
/// </summary>
public interface IMountResource : IApplicationResource
{
    /// <summary>
    /// The mounts this resource requires.
    /// </summary>
    IReadOnlyList<ResourceMount> Mounts { get; }
}
