using System;
using System.Collections.Generic;
using System.Collections.Concurrent;

namespace Assimalign.Extensions.Mapping;

using Assimalign.Extensions.Mapping.Internal;

public sealed class MapperFactoryBuilder
{

    public MapperFactoryBuilder()
    {
        this.Mappers = new ConcurrentDictionary<string, IMapper>();
    }

    internal ConcurrentDictionary<string, IMapper> Mappers { get; }


    /// <summary>
    /// 
    /// </summary>
    /// <param name="mapperName"></param>
    /// <param name="configure"></param>
    /// <returns></returns>
    public MapperFactoryBuilder AddMapper(string mapperName, Action<MapperBuilder> configure)
    {
        Mappers.GetOrAdd(mapperName, name =>
        {
            var builder = new MapperBuilder();

            configure.Invoke(builder);

            return new Mapper(builder.Profiles, builder.Options);
        });

        return this;
    }

    public MapperFactoryBuilder AddMapper(string mapperName, IMapperProfileBuilder builder)
    {
        Mappers.GetOrAdd(mapperName, new Mapper(builder.Build(), new MapperOptions()));
        return this;
    }

    public MapperFactoryBuilder AddMapper(string mapperName, IMapperProfileBuilder builder, Action<MapperOptions> configure)
    {
        Mappers.GetOrAdd(mapperName, name =>
        {
            var options = new MapperOptions();

            configure.Invoke(options);

            return new Mapper(builder.Build(), options);
        });

        return this;
    }

    public MapperFactoryBuilder AddMapper(string mapperName, Action<IMapperProfileBuilder> configure)
    {
        Mappers.GetOrAdd(mapperName, name =>
        {
            var builder = new MapperProfileBuilderDefault(configure) as IMapperProfileBuilder;
            return new Mapper(builder.Build(), new MapperOptions());
        });

        return this;
    }

    public MapperFactoryBuilder AddMapper(string mapperName, IEnumerable<IMapperProfile> profiles)
    {
        Mappers.GetOrAdd(mapperName, new Mapper(profiles, new MapperOptions()));

        return this;
    }
}
