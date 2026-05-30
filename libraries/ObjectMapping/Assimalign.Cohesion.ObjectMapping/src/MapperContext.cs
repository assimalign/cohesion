using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ObjectMapping;

/// <summary>
/// 
/// </summary>
public sealed class MapperContext : IMapperContext
{
	private readonly object _target;
	private readonly object _source;

	/// <summary>
	/// 
	/// </summary>
	/// <param name="target"></param>
	/// <param name="source"></param>
	/// <exception cref="ArgumentNullException"></exception>
	public MapperContext(object target, object source)
	{
		_target = ArgumentNullException.ThrowIfNull<object>(target);
		_source = ArgumentNullException.ThrowIfNull<object>(source);
	}

	/// <inheritdoc cref="IMapperContext.Source"/>
	public object Target => _target;

	/// <inheritdoc cref="IMapperContext.Source"/>
	public object Source => _source;

	/// <inheritdoc cref="IMapperContext.CollectionHandling"/>
	public MapperIgnoreHandling IgnoreHandling { get; init; } = MapperIgnoreHandling.Never;

    /// <inheritdoc cref="IMapperContext.CollectionHandling"/>
    public MapperCollectionHandling CollectionHandling { get; init; }

	/// <inheritdoc cref="IMapperContext.Profiles"/>
	public required IReadOnlyList<IMapperProfile> Profiles { get; init; }
}