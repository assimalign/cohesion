using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.ObjectMapping;

/// <summary>
/// 
/// </summary>
/// <typeparam name="TTarget"></typeparam>
/// <typeparam name="TSource"></typeparam>
public abstract class MapperProfile<
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TTarget,
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TSource> : IMapperProfile
{
    private readonly List<IMapperAction> _mapActions = new List<IMapperAction>();

    public MapperProfile()
    {
        Configure(new MapperProfileDescriptor<TTarget, TSource>(this, _mapActions));
    }

    /// <inheritdoc />
    public Type TargetType => typeof(TTarget);

    /// <inheritdoc />
    public Type SourceType => typeof(TSource);

    /// <inheritdoc />
    public IReadOnlyList<IMapperAction> MapActions => _mapActions;

    /// <summary>
    /// 
    /// </summary>
    /// <param name="descriptor"></param>
    protected abstract void Configure(MapperProfileDescriptor<TTarget, TSource> descriptor);
}
