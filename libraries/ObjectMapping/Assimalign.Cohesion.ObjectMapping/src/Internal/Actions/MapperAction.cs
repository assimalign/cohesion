using System;

namespace Assimalign.Cohesion.ObjectMapping.Internal;

internal class MapperAction : IMapperAction
{
    private readonly Action<IMapperContext> _configure;
    
    public MapperAction(Action<IMapperContext> configure)
    {
        _configure = configure;
    }

    public virtual void Invoke(IMapperContext context)
    {
        _configure.Invoke(context);
    }
}