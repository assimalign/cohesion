using System;
using System.Collections;
using System.Collections.Generic;

namespace Assimalign.Extensions.Mapping.Internal;

internal sealed class MapperProfileDefault<TTarget, TSource> : MapperProfile<TTarget, TSource>
{
    private readonly Action<IMapperActionDescriptor<TTarget, TSource>> configure;

    public MapperProfileDefault(Action<IMapperActionDescriptor<TTarget, TSource>> configure)
    {
        this.configure = configure;
    }


    public override void Configure(IMapperActionDescriptor<TTarget, TSource> descriptor)
    {
        configure.Invoke(descriptor);
    }
}