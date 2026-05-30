using System;

namespace Assimalign.Cohesion.ObjectMapping;

/// <summary>
/// Collects named mappers and builds an <see cref="IMapperFactory"/>.
/// </summary>
public interface IMapperFactoryBuilder
{
    /// <summary>
    /// Adds a pre-built mapper, keyed by its <see cref="IMapper.Name"/>.
    /// </summary>
    /// <param name="mapper">The mapper to add.</param>
    /// <returns>The same builder instance for chaining.</returns>
    IMapperFactoryBuilder AddMapper(IMapper mapper);

    /// <summary>
    /// Adds a mapper produced by a factory callback, keyed by its <see cref="IMapper.Name"/>.
    /// </summary>
    /// <param name="factory">A callback that builds the mapper from an <see cref="IMapperBuilder"/>.</param>
    /// <returns>The same builder instance for chaining.</returns>
    IMapperFactoryBuilder AddMapper(Func<IMapperBuilder, IMapper> factory);

    /// <summary>
    /// Builds the configured <see cref="IMapperFactory"/>.
    /// </summary>
    /// <returns>The configured factory.</returns>
    IMapperFactory Build();
}
