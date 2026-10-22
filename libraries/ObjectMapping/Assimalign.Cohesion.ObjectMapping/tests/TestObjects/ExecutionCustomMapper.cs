using System;

namespace Assimalign.Cohesion.ObjectMapping.Tests;

/// <summary>A custom mapper that records the primitive dispatch and returns its own result.</summary>
public sealed class ExecutionCustomMapper : IMapper
{
    public string Name => "Custom";
    public object? LastSource { get; private set; }
    public object? LastTarget { get; private set; }
    public Type? LastSourceType { get; private set; }
    public Type? LastTargetType { get; private set; }
    public ExecutionTarget Result { get; } = new ExecutionTarget();

    public object Map(object target, object source, Type targetType, Type sourceType)
    {
        LastTarget = target;
        LastSource = source;
        LastTargetType = targetType;
        LastSourceType = sourceType;
        return Result;
    }
}
