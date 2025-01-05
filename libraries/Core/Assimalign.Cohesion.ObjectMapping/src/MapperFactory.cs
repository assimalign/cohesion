using System;
using System.Collections.Generic;
using System.Collections.Concurrent;

namespace Assimalign.Extensions.Mapping;

using Assimalign.Extensions.Mapping.Internal;

/// <summary>
/// 
/// </summary>
public sealed class MapperFactory : IMapperFactory
{
    private readonly IDictionary<string, IMapper> mappers;
    
    internal MapperFactory(IDictionary<string, IMapper> mappers)
    {
        this.mappers = mappers;
    }

    IMapper IMapperFactory.CreateMapper(string mapperName)
    {
        return this.mappers[mapperName];
    }


    /// <summary>
    /// 
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public static IMapperFactory Configure(Action<MapperFactoryBuilder> configure)
    {
        var builder = new MapperFactoryBuilder();

        configure.Invoke(builder);

        return new MapperFactory(builder.Mappers);
    }
}
