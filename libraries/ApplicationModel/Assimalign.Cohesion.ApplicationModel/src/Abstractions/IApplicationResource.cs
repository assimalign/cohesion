using System;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// 
/// </summary>
public interface IApplicationResource
{
    /// <summary>
    /// The unique resource ID.
    /// </summary>
    ResourceId Id => Guid.AsDeterministicGuid(Name);

    /// <summary>
    /// The name of the resource, which should be unique within the application context. This name
    /// is used to generate the resource ID and should be descriptive of the resource's purpose or function.
    /// </summary>
    ResourceName Name { get; }
}