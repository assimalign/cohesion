using System;

namespace Assimalign.Cohesion.ObjectMapping.Internal;

internal class MapperAction : IMapperAction
{
    private readonly Action<IMapperContext> configure;
    public MapperAction(Action<IMapperContext> configure) => this.configure = configure;
    public int Id => this.GetHashCode();
    public virtual void Invoke(IMapperContext context) => configure.Invoke(context);
}

internal sealed class MapperAction<TTarget, TSource> : MapperAction
{
    public MapperAction(Action<TTarget, TSource> configure) : base(context =>
        {
            if (context.Target is TTarget target && context.Source is TSource source)
            {
                configure.Invoke(target, source);
            }
        }) { }
}