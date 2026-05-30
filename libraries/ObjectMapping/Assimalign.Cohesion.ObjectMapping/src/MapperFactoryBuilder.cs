using System;
using System.Collections.Concurrent;

namespace Assimalign.Cohesion.ObjectMapping;

/// <summary>
/// 
/// </summary>
/// <remarks>
/// Use a factory to separate mappers that may have conflicting mapping profiles for the same source and destination types.
/// </remarks>
public sealed class MapperFactoryBuilder : IMapperFactoryBuilder
{
    private readonly ConcurrentDictionary<string, IMapper> _mappers;

    public MapperFactoryBuilder()
    {
        _mappers = new ConcurrentDictionary<string, IMapper>();
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="mapperName"></param>
    /// <param name="configure"></param>
    /// <returns></returns>
    public MapperFactoryBuilder AddMapper(string mapperName, Func<MapperBuilder, Mapper> configure)
    {
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
    /// 
    /// </summary>
    /// <param name="mapperName"></param>
    /// <param name="ignoreHandling"></param>
    /// <param name="configure"></param>
    /// <returns></returns>
    public MapperFactoryBuilder AddMapper(string mapperName, MapperIgnoreHandling ignoreHandling, Func<MapperBuilder, Mapper> configure)
    {
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
    /// 
    /// </summary>
    /// <param name="mapperName"></param>
    /// <param name="collectionHandling"></param>
    /// <param name="configure"></param>
    /// <returns></returns>
    public MapperFactoryBuilder AddMapper(string mapperName, MapperCollectionHandling collectionHandling, Func<MapperBuilder, Mapper> configure)
    {
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
    /// 
    /// </summary>
    /// <param name="mapperName"></param>
    /// <param name="ignoreHandling"></param>
    /// <param name="collectionHandling"></param>
    /// <param name="configure"></param>
    /// <returns></returns>
    public MapperFactoryBuilder AddMapper(string mapperName, MapperIgnoreHandling ignoreHandling, MapperCollectionHandling collectionHandling, Func<MapperBuilder, Mapper> configure)
    {
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

        _mappers.GetOrAdd(Guid.NewGuid().ToString(), name =>
        {
            var builder = new MapperBuilder(new MapperOptions()
            {
                Name = name
            });
            return factory.Invoke(builder);
        });
        return this;
    }

    private class MapperFactory : IMapperFactory
    {
        private readonly ConcurrentDictionary<string, IMapper> _mappers;

        internal MapperFactory(ConcurrentDictionary<string, IMapper> mappers)
        {
            _mappers = mappers;
        }

        public IMapper Create(string mapperName)
        {
            return _mappers[mapperName];
        }
    }
}
