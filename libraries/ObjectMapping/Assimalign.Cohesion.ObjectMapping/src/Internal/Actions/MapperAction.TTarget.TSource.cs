using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.ObjectMapping.Internal;

internal sealed class MapperAction<TTarget, TSource> : MapperAction
{
    public MapperAction(Action<TTarget, TSource> configure) : base(context =>
    {
        if (context.Target is TTarget target && context.Source is TSource source)
        {
            configure.Invoke(target, source);
        }
    })
    { }
}
