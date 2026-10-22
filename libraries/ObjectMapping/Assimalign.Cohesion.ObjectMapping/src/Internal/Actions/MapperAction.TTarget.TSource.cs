using System;

namespace Assimalign.Cohesion.ObjectMapping.Internal;

internal sealed class MapperAction<TTarget, TSource> : TypedMapperAction<TTarget, TSource>
{
    private readonly Action<TTarget, TSource> _configure;

    public MapperAction(Action<TTarget, TSource> configure)
    {
        _configure = configure;
    }

    public override void Invoke(IMapperContext context)
    {
        if (context.Target is TTarget target && context.Source is TSource source)
        {
            _configure.Invoke(target, source);
        }
    }

    public override void Invoke(TTarget target, TSource source, ref MapperExecution execution)
    {
        _configure.Invoke(target, source);
    }
}
