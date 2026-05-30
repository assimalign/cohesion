using System;
using System.Collections.Concurrent;

namespace Assimalign.Cohesion.ObjectMapping;

/// <summary>
/// Builds an <see cref="IMapperFactory"/> from one or more named mappers.
/// </summary>
/// <remarks>
/// Use a factory to separate mappers that may have conflicting mapping profiles for the same source and target types.
/// </remarks>
public sealed class MapperFactoryBuilder : IMapperFactoryBuilder
{
    private readonly ConcurrentDictionary<string, IMapper> _mappers;

    /// <summary>
    /// Initializes a new instance of the <see cref="MapperFactoryBuilder"/> class.
    /// </summary>
    public MapperFactoryBuilder()
    {
        _mappers = new ConcurrentDictionary<string, IMapper>();
    }

    /// <summary>
    /// Adds a named mapper configured through a <see cref="MapperBuilder"/>.
    /// </summary>
    /// <param name="mapperName">The unique name the mapper is addressable by.</param>
    /// <param name="configure">A callback that configures and builds the mapper.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="mapperName"/> or <paramref name="configure"/> is <see langword="null"/>.</exception>
    public MapperFactoryBuilder AddMapper(string mapperName, Func<MapperBuilder, Mapper> configure)
    {
        ArgumentNullException.ThrowIfNull(mapperName);
        ArgumentNullException.ThrowIfNull(configure);

        _mappers.GetOrAdd(mapperName, name =>
        {
            var builder = new MapperBuilder(new MapperOptions()
            {
                Name = name
            });

            return configure.Invoke(builder);
        });

        return this;
    }

    /// <summary>
    /// Adds a named mapper with a specific null/default handling strategy.
    /// </summary>
    /// <param name="mapperName">The unique name the mapper is addressable by.</param>
    /// <param name="ignoreHandling">The null/default handling strategy for the mapper.</param>
    /// <param name="configure">A callback that configures and builds the mapper.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="mapperName"/> or <paramref name="configure"/> is <see langword="null"/>.</exception>
    public MapperFactoryBuilder AddMapper(string mapperName, MapperIgnoreHandling ignoreHandling, Func<MapperBuilder, Mapper> configure)
    {
        ArgumentNullException.ThrowIfNull(mapperName);
        ArgumentNullException.ThrowIfNull(configure);

        _mappers.GetOrAdd(mapperName, name =>
        {
            var builder = new MapperBuilder(new MapperOptions()
            {
                Name = name,
                IgnoreHandling = ignoreHandling
            });

            return configure.Invoke(builder);
        });

        return this;
    }

    /// <summary>
    /// Adds a named mapper with a specific collection handling strategy.
    /// </summary>
    /// <param name="mapperName">The unique name the mapper is addressable by.</param>
    /// <param name="collectionHandling">The collection handling strategy for the mapper.</param>
    /// <param name="configure">A callback that configures and builds the mapper.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="mapperName"/> or <paramref name="configure"/> is <see langword="null"/>.</exception>
    public MapperFactoryBuilder AddMapper(string mapperName, MapperCollectionHandling collectionHandling, Func<MapperBuilder, Mapper> configure)
    {
        ArgumentNullException.ThrowIfNull(mapperName);
        ArgumentNullException.ThrowIfNull(configure);

        _mappers.GetOrAdd(mapperName, name =>
        {
            var builder = new MapperBuilder(new MapperOptions()
            {
                Name = name,
                CollectionHandling = collectionHandling
            });

            return configure.Invoke(builder);
        });

        return this;
    }

    /// <summary>
    /// Adds a named mapper with specific null/default and collection handling strategies.
    /// </summary>
    /// <param name="mapperName">The unique name the mapper is addressable by.</param>
    /// <param name="ignoreHandling">The null/default handling strategy for the mapper.</param>
    /// <param name="collectionHandling">The collection handling strategy for the mapper.</param>
    /// <param name="configure">A callback that configures and builds the mapper.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="mapperName"/> or <paramref name="configure"/> is <see langword="null"/>.</exception>
    public MapperFactoryBuilder AddMapper(string mapperName, MapperIgnoreHandling ignoreHandling, MapperCollectionHandling collectionHandling, Func<MapperBuilder, Mapper> configure)
    {
        ArgumentNullException.ThrowIfNull(mapperName);
        ArgumentNullException.ThrowIfNull(configure);

        _mappers.GetOrAdd(mapperName, name =>
        {
            var builder = new MapperBuilder(new MapperOptions()
            {
                Name = name,
                IgnoreHandling = ignoreHandling,
                CollectionHandling = collectionHandling
            });

            return configure.Invoke(builder);
        });

        return this;
    }

    /// <summary>
    /// Builds an <see cref="IMapperFactory"/> over the mappers registered so far.
    /// </summary>
    /// <returns>A factory that resolves the registered mappers by name.</returns>
    public IMapperFactory Build()
    {
        return new MapperFactory(_mappers);
    }

    IMapperFactoryBuilder IMapperFactoryBuilder.AddMapper(IMapper mapper)
    {
        ArgumentNullException.ThrowIfNull(mapper);

        _mappers.GetOrAdd(mapper.Name, mapper);

        return this;
    }

    IMapperFactoryBuilder IMapperFactoryBuilder.AddMapper(Func<IMapperBuilder, IMapper> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var mapper = factory.Invoke(new MapperBuilder());

        _mappers.GetOrAdd(mapper.Name, mapper);

        return this;
    }

    private sealed class MapperFactory : IMapperFactory
    {
        private readonly ConcurrentDictionary<string, IMapper> _mappers;

        internal MapperFactory(ConcurrentDictionary<string, IMapper> mappers)
        {
            _mappers = mappers;
        }

        public IMapper Create(string mapperName)
        {
            ArgumentNullException.ThrowIfNull(mapperName);

            if (_mappers.TryGetValue(mapperName, out var mapper))
            {
                return mapper;
            }

            throw new MapperException($"No mapper has been registered with the name '{mapperName}'.");
        }
    }
}
