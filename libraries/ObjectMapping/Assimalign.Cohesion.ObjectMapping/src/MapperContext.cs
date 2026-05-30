using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ObjectMapping;

/// <summary>
/// The default <see cref="IMapperContext"/> implementation passed to mapping actions.
/// </summary>
public sealed class MapperContext : IMapperContext
{
    private readonly object _target;
    private readonly object _source;

    /// <summary>
    /// Initializes a new <see cref="MapperContext"/> for a target/source pair.
    /// </summary>
    /// <param name="target">The instance being populated.</param>
    /// <param name="source">The instance values are read from.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="target"/> or <paramref name="source"/> is <see langword="null"/>.</exception>
    public MapperContext(object target, object source)
    {
        _target = ArgumentNullException.ThrowIfNull<object>(target);
        _source = ArgumentNullException.ThrowIfNull<object>(source);
    }

    /// <inheritdoc cref="IMapperContext.Target"/>
    public object Target => _target;

    /// <inheritdoc cref="IMapperContext.Source"/>
    public object Source => _source;

    /// <inheritdoc cref="IMapperContext.IgnoreHandling"/>
    public MapperIgnoreHandling IgnoreHandling { get; init; } = MapperIgnoreHandling.Never;

    /// <inheritdoc cref="IMapperContext.CollectionHandling"/>
    public MapperCollectionHandling CollectionHandling { get; init; }

    /// <inheritdoc cref="IMapperContext.Profiles"/>
    public required IReadOnlyList<IMapperProfile> Profiles { get; init; }
}
