using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.ObjectMapping;

/// <summary>
/// Base class for a strongly typed mapping profile from <typeparamref name="TSource"/>
/// to <typeparamref name="TTarget"/>. Derive from this type and implement
/// <see cref="Configure"/> to declare the mapping actions.
/// </summary>
/// <typeparam name="TTarget">The target type the profile maps to.</typeparam>
/// <typeparam name="TSource">The source type the profile maps from.</typeparam>
public abstract class MapperProfile<
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TTarget,
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TSource> : IMapperProfile
{
    private readonly List<IMapperAction> _mapActions = new List<IMapperAction>();

    /// <summary>
    /// An optional inline configuration delegate. Assigned before <see cref="Configure"/>
    /// is invoked so derived profiles that rely on it (e.g. the inline-configured profile
    /// produced by <c>AddProfile&lt;TTarget, TSource&gt;</c>) can read it during construction.
    /// </summary>
    private protected Action<MapperProfileDescriptor<TTarget, TSource>>? Configurator { get; }

    /// <summary>
    /// Initializes the profile. Prefers source-generated configuration via
    /// <see cref="TryConfigureGenerated"/>; falls back to the reflection-based
    /// <see cref="Configure"/> when no generated configuration is available.
    /// </summary>
    protected MapperProfile()
    {
        var descriptor = new MapperProfileDescriptor<TTarget, TSource>(this, _mapActions);

        if (!TryConfigureGenerated(descriptor))
        {
            Configure(descriptor);
        }
    }

    private protected MapperProfile(Action<MapperProfileDescriptor<TTarget, TSource>> configurator)
    {
        Configurator = configurator;
        Configure(new MapperProfileDescriptor<TTarget, TSource>(this, _mapActions));
    }

    /// <inheritdoc />
    public Type TargetType => typeof(TTarget);

    /// <inheritdoc />
    public Type SourceType => typeof(TSource);

    /// <inheritdoc />
    public IReadOnlyList<IMapperAction> MapActions => _mapActions;

    /// <summary>
    /// Declares the mapping actions for this profile using the supplied descriptor.
    /// </summary>
    /// <param name="descriptor">The descriptor used to register mapping actions.</param>
    protected abstract void Configure(MapperProfileDescriptor<TTarget, TSource> descriptor);

    /// <summary>
    /// Infrastructure for the object-mapping source generator. The generated partial profile
    /// overrides this to register AOT-safe, delegate-based mapping actions; returning
    /// <see langword="true"/> suppresses the reflection-based <see cref="Configure"/> path.
    /// The default implementation returns <see langword="false"/>.
    /// </summary>
    /// <param name="descriptor">The descriptor used to register mapping actions.</param>
    /// <returns><see langword="true"/> when generated configuration was applied; otherwise <see langword="false"/>.</returns>
    protected virtual bool TryConfigureGenerated(MapperProfileDescriptor<TTarget, TSource> descriptor) => false;
}
