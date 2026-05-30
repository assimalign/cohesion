using System;

namespace Assimalign.Cohesion.ObjectMapping;

public interface IMapperFactoryBuilder
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="mapper"></param>
    /// <returns></returns>
    IMapperFactoryBuilder AddMapper(IMapper mapper);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="factory"></param>
    /// <returns></returns>
    IMapperFactoryBuilder AddMapper(Func<IMapperBuilder, IMapper> factory);

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    IMapperFactory Build();
}
