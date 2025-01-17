using System;
using System.Reflection;
using System.Linq.Expressions;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ObjectMapping.Internal;

using Assimalign.Cohesion.ObjectMapping.Internal.Exceptions;


internal class MapperActionDescriptor : IMapperActionDescriptor
{
    public IMapperActionStack MapActions { get; init; }

    public IMapperActionDescriptor MapAction(IMapperAction action)
    {
        throw new NotImplementedException();
    }
}


internal class MapperActionDescriptor<TTarget, TSource> : IMapperActionDescriptor<TTarget, TSource>
{
    public IMapperActionStack MapActions { get; init; }
    public IList<IMapperProfile> Profiles { get; init; } // Passing all added profiles from options as reference to be able to register nested profiles

    IMapperActionDescriptor IMapperActionDescriptor.MapAction(IMapperAction action) => MapAction(action);
    
    public IMapperActionDescriptor<TTarget, TSource> MapAction(IMapperAction action) 
    {
        MapActions.Push(action);
        return this;
    }
    public IMapperActionDescriptor<TTarget, TSource> MapAction(Action<IMapperContext> configure)
    {
        return this.MapAction(new MapperAction(configure));
    }
    public IMapperActionDescriptor<TTarget, TSource> MapAction(Action<TTarget, TSource> configure)
    {
        return this.MapAction(new MapperAction<TTarget, TSource>(configure));
    }

    public IMapperActionDescriptor<TTarget, TSource> MapMember<TTargetMember, TSourceMember>(Expression<Func<TTarget, TTargetMember>> target, Expression<Func<TSource, TSourceMember>> source)
    {
        var mapperAction = new MapperActionMember<TTarget, TTargetMember, TSource, TSourceMember>(target, source);

        // Let's ensure we are not adding an already mapped action 
        if (MapActions.Contains(mapperAction))
        {
            throw new MapperInvalidMappingException(target);
        }

        return this.MapAction(mapperAction);
    }
}