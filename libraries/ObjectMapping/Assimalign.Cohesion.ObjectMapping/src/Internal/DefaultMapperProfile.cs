using System;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.ObjectMapping.Internal;

/// <summary>
/// A profile whose mapping actions are supplied inline via a configuration delegate,
/// used by the <c>AddProfile&lt;TTarget, TSource&gt;</c> builder overloads.
/// </summary>
internal sealed class DefaultMapperProfile<
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TTarget,
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TSource> : MapperProfile<TTarget, TSource>
{
    // The delegate is passed to the base constructor (and null-checked there) so it is
    // assigned before the base constructor dispatches the virtual Configure call.
    public DefaultMapperProfile(Action<MapperProfileDescriptor<TTarget, TSource>> configure)
        : base(ArgumentNullException.ThrowIfNull<Action<MapperProfileDescriptor<TTarget, TSource>>>(configure))
    {
    }

    protected override void Configure(MapperProfileDescriptor<TTarget, TSource> descriptor)
    {
        Configurator!.Invoke(descriptor);
    }
}
