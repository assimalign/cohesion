using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ObjectMapping;

/// <summary>
/// 
/// </summary>
public sealed class MapperFactory : IMapperFactory
{
    private readonly IDictionary<string, IMapper> _mappers;
    
    internal MapperFactory(IDictionary<string, IMapper> mappers)
    {
        _mappers = mappers;
    }

    IMapper IMapperFactory.Create(string mapperName)
    {
        return _mappers[mapperName];
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public static IMapperFactory Configure(Action<MapperFactoryBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new MapperFactoryBuilder();

        configure.Invoke(builder);

        return new MapperFactory(builder.Mappers);
    }
}
