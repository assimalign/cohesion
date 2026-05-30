using System;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.ObjectMapping.Internal;

internal sealed class DefaultMapperProfile<
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TTarget,
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TSource> : MapperProfile<TTarget, TSource>
{
    private readonly Action<MapperProfileDescriptor<TTarget, TSource>> _configure;

    public DefaultMapperProfile(Action<MapperProfileDescriptor<TTarget, TSource>> configure)
    {
        _configure = ArgumentNullException.ThrowIfNull<Action<MapperProfileDescriptor<TTarget, TSource>>>(configure);
    }

    protected override void Configure(MapperProfileDescriptor<TTarget, TSource> descriptor)
    {
        _configure.Invoke(descriptor);
    }
}