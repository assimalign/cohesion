using System;
using System.Collections.Generic;

namespace Assimalign.Extensions.Mapping;

///<summary>
///
///</summary>
public interface IMapperFactory
{

    /// <summary>
    /// 
    /// </summary>
    /// <param name="mapperName"></param>
    /// <returns></returns>
    IMapper CreateMapper(string mapperName);

    ///// <summary>
    ///// Creates a mapper
    ///// </summary>
    ///// <param name="mapperName"></param>
    ///// <param name="builder"></param>
    ///// <returns></returns>
    //IMapper Create(string mapperName, IMapperProfileBuilder builder);

    ///// <summary>
    ///// 
    ///// </summary>
    ///// <param name="mapperName"></param>
    ///// <param name="configure"></param>
    ///// <returns></returns>
    //IMapper Create(string mapperName, Action<IMapperProfileBuilder> configure);

    ///// <summary>
    ///// 
    ///// </summary>
    ///// <param name="mapperName"></param>
    ///// <param name="profiles"></param>
    ///// <returns></returns>
    //IMapper Create(string mapperName, IEnumerable<IMapperProfile> profiles);
}