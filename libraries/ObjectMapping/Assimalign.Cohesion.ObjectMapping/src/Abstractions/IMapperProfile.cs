using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ObjectMapping;

/// <summary>
/// 
/// </summary>
public interface IMapperProfile
{
    /// <summary>
    /// Represents the Target Type in which values will be copied to.
    /// </summary>
    Type TargetType { get; }

    /// <summary>
    /// Represents the Source Type to be used to copy values to the target type.
    /// </summary>
    Type SourceType { get; }

    /// <summary>
    /// A collection of actions to be invoked on mapping.
    /// </summary>
    IReadOnlyList<IMapperAction> MapActions { get; }
}