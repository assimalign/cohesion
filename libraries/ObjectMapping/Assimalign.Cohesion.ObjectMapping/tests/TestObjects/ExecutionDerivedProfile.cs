using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ObjectMapping.Tests;

/// <summary>A derived built-in profile that replaces its interface action list.</summary>
public sealed class ExecutionDerivedProfile : MapperProfile<ExecutionTarget, ExecutionSource>, IMapperProfile
{
    public IReadOnlyList<IMapperAction> ReplacementActions { get; set; } = Array.Empty<IMapperAction>();
    IReadOnlyList<IMapperAction> IMapperProfile.MapActions => ReplacementActions;

    protected override void Configure(MapperProfileDescriptor<ExecutionTarget, ExecutionSource> descriptor)
    {
        descriptor.MapMember(static source => source.Value, static (target, value) => target.Value = value);
    }
}
