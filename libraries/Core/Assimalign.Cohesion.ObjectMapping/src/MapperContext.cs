using System.Collections.Generic;

namespace Assimalign.Extensions.Mapping;

/// <summary>
/// 
/// </summary>
public sealed class MapperContext : IMapperContext
{
	private readonly object target;
	private readonly object source;

	public MapperContext(object target, object source)
	{
		this.target = target;
		this.source = source;
	}

	/// <inheritdoc cref="IMapperContext.Source"/>
	public object Target => this.target;

	/// <inheritdoc cref="IMapperContext.Source"/>
	public object Source => this.source;

	/// <inheritdoc cref="IMapperContext.CollectionHandling"/>
	public MapperIgnoreHandling IgnoreHandling { get; init; }

	/// <inheritdoc cref="IMapperContext.CollectionHandling"/>
    public MapperCollectionHandling CollectionHandling { get; init; }

	/// <inheritdoc cref="IMapperContext.Profiles"/>
	public IEnumerable<IMapperProfile> Profiles { get; init; }
}
