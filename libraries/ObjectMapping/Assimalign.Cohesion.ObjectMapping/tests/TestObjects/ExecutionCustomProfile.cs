using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ObjectMapping.Tests;

/// <summary>A custom profile whose action list is supplied by the caller.</summary>
public sealed class ExecutionCustomProfile : IMapperProfile
{
    public Type TargetType => typeof(ExecutionTarget);
    public Type SourceType => typeof(ExecutionSource);
    public IReadOnlyList<IMapperAction> MapActions { get; set; } = Array.Empty<IMapperAction>();
}
