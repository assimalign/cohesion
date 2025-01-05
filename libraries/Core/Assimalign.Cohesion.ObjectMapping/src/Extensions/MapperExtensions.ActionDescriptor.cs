
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Extensions.Mapping;

using Assimalign.Extensions.Mapping.Internal;
using Assimalign.Extensions.Mapping.Internal.Exceptions;
using System.Diagnostics.CodeAnalysis;

public static class MapperActionDescriptorExtensions
{
    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="TTarget"></typeparam>
    /// <typeparam name="TSource"></typeparam>
    /// <param name="descriptor"></param>
    /// <param name="configure"></param>
    /// <returns></returns>
    public static IMapperActionDescriptor<TTarget, TSource> MapAction<TTarget, TSource>(this IMapperActionDescriptor<TTarget, TSource> descriptor, Action<IMapperContext> configure)
    {
        descriptor.MapAction(new MapperAction(configure));

        return descriptor;
    }
   
    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="TTarget"></typeparam>
    /// <typeparam name="TSource"></typeparam>
    /// <param name="descriptor"></param>
    /// <param name="configure"></param>
    /// <returns></returns>
    public static IMapperActionDescriptor<TTarget, TSource> MapAction<TTarget, TSource>(this IMapperActionDescriptor<TTarget, TSource> descriptor, Action<TTarget, TSource> configure)
    {
        descriptor.MapAction(new MapperAction<TTarget, TSource>(configure));

        return descriptor;
    }

    /// <summary>
    /// Maps a property by string name from the <paramref name="source"/> type 
    /// to the <paramref name="target"/> type.
    /// </summary>
    /// <typeparam name="TTarget">The target type to be mapped.</typeparam>
    /// <typeparam name="TSource">The source type to be mapped.</typeparam>
    /// <param name="descriptor"></param>
    /// <param name="target">The property name within the <typeparamref name="TTarget"/>.</param>
    /// <param name="source">The property name within the <typeparamref name="TSource"/>.</param>
    /// <returns></returns>
    /// <exception cref="MapperInvalidMappingException"></exception>
    
    [RequiresUnreferencedCode("")]
    public static IMapperActionDescriptor<TTarget, TSource> MapMember<TTarget, TSource>(this IMapperActionDescriptor<TTarget, TSource> descriptor, string target, string source)
    {
        var targetParameter = Expression.Parameter(typeof(TTarget));
        var sourceParameter = Expression.Parameter(typeof(TSource));
        var targetParameterMember = targetParameter.GetMemberExpression(target);
        var sourceParameterMember = sourceParameter.GetMemberExpression(source);
        var targetLambda = Expression.Lambda(targetParameterMember, targetParameter);
        var sourceLambda = Expression.Lambda(sourceParameterMember, sourceParameter);

        var mapperActionType = typeof(MapperActionMember<,,,>).MakeGenericType(
            typeof(TTarget),
            targetParameterMember.Type,
            typeof(TSource),
            sourceParameterMember.Type);

        var mapperAction = Activator.CreateInstance(mapperActionType, targetLambda, sourceLambda) as IMapperAction;

        // Let's ensure we are not adding an already mapped action 
        if (descriptor.MapActions.Contains(mapperAction))
        {
            throw new MapperInvalidMappingException(targetLambda);
        }

        descriptor.MapAction(mapperAction);

        return descriptor;
    }

    /// <summary>
    /// Create a pointer map action for two complex members of 
    /// type <typeparamref name="TSource"/> and <typeparamref name="TTarget"/>.
    /// </summary>
    /// <typeparam name="TTarget"></typeparam>
    /// <typeparam name="TTargetMember"></typeparam>
    /// <typeparam name="TSource"></typeparam>
    /// <typeparam name="TSourceMember"></typeparam>
    /// <param name="descriptor"></param>
    /// <param name="target"></param>
    /// <param name="source"></param>
    /// <returns></returns>
    public static IMapperActionDescriptor<TTarget, TSource> MapMemberTypes<TTarget, TTargetMember, TSource, TSourceMember>(this IMapperActionDescriptor<TTarget, TSource> descriptor, Expression<Func<TTarget, TTargetMember>> target, Expression<Func<TSource, TSourceMember>> source)
        where TTargetMember : class, new()
        where TSourceMember : class, new()
    {
        var mapperAction = new MapperActionMemberType<TTarget, TTargetMember, TSource, TSourceMember>(target, source);

        descriptor.MapAction(mapperAction);

        return descriptor;
    }

    /// <summary>
    /// Create a pointer map action for two enumerable members of 
    /// type <typeparamref name="TSource"/> and <typeparamref name="TTarget"/>.
    /// </summary>
    /// <typeparam name="TTarget"></typeparam>
    /// <typeparam name="TTargetMember"></typeparam>
    /// <typeparam name="TSource"></typeparam>
    /// <typeparam name="TSourceMember"></typeparam>
    /// <param name="descriptor"></param>
    /// <param name="target"></param>
    /// <param name="source"></param>
    /// <returns></returns>
    /// <exception cref="Exception"></exception>
    /// <exception cref="MapperInvalidMappingException"></exception>
    public static IMapperActionDescriptor<TTarget, TSource> MapMemberEnumerables<TTarget, TTargetMember, TSource, TSourceMember>(this IMapperActionDescriptor<TTarget, TSource> descriptor, Expression<Func<TTarget, IEnumerable<TTargetMember>>> target, Expression<Func<TSource, IEnumerable<TSourceMember>>> source)
        where TTargetMember : class, new()
        where TSourceMember : class, new()
    {
        var mapperAction = new MapperActionMemberEnumerable<TTarget, TTargetMember, TSource, TSourceMember>(target, source);

        descriptor.MapAction(mapperAction);
       
        return descriptor;
    }

    /// <summary>
    /// Tries to map all Members of <typeparamref name="TTarget"/> and <typeparamref name="TSource"/> that share the same name.
    /// </summary>
    /// <returns></returns>
    public static IMapperActionDescriptor<TTarget, TSource> MapAllMembers<TTarget, TSource>(this IMapperActionDescriptor<TTarget, TSource> descriptor)
    {
        return descriptor.MapAllProperties().MapAllFields();
    }

    /// <summary>
    /// Tries to map only Field Members of <typeparamref name="TTarget"/> and <typeparamref name="TSource"/> that share the same name.
    /// </summary>
    /// <returns></returns>
    public static IMapperActionDescriptor<TTarget, TSource> MapAllFields<TTarget, TSource>(this IMapperActionDescriptor<TTarget, TSource> descriptor)
    {
        var targetType = typeof(TTarget);
        var sourceType = typeof(TSource);

        var targetParameter = Expression.Parameter(targetType);
        var sourceParameter = Expression.Parameter(sourceType);

        foreach (var targetField in targetType.GetFields().Where(x => x.IsPublic))
        {
            var sourceField = sourceType.GetField(
                targetField.Name,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

            if (sourceField is not null && sourceField.FieldType == targetField.FieldType)
            {
                var targetParameterMember = Expression.Field(targetParameter, targetField);
                var sourceParameterMember = Expression.Field(sourceParameter, sourceField);
                var targetLambda = Expression.Lambda(targetParameterMember, targetParameter);
                var sourceLambda = Expression.Lambda(sourceParameterMember, sourceParameter);
                var mapperActionType = typeof(MapperActionMember<,,,>).MakeGenericType(
                    typeof(TTarget),
                    targetParameterMember.Type,
                    typeof(TSource),
                    sourceParameterMember.Type);

                var mapperAction = Activator.CreateInstance(mapperActionType, targetLambda, sourceLambda) as IMapperAction;

                if (descriptor.MapActions.Contains(mapperAction))
                {
                    throw new MapperInvalidMappingException(targetLambda);
                }
                descriptor.MapAction(mapperAction);
            }
        }

        return descriptor;
    }

    /// <summary>
    /// Tries to map only Property Members of <typeparamref name="TTarget"/> and <typeparamref name="TSource"/> that share the same name.
    /// </summary>
    /// <returns></returns>
    public static IMapperActionDescriptor<TTarget, TSource> MapAllProperties<TTarget, TSource>(this IMapperActionDescriptor<TTarget, TSource> descriptor)
    {
        var targetType = typeof(TTarget);
        var sourceType = typeof(TSource);

        var targetParameter = Expression.Parameter(targetType);
        var sourceParameter = Expression.Parameter(sourceType);

        foreach (var targetProperty in targetType.GetProperties().Where(x => x.CanRead && x.CanWrite))
        {
            var sourceProperty = sourceType.GetProperty(
                targetProperty.Name,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

            if (sourceProperty is not null && sourceProperty.CanRead && sourceProperty.CanWrite && sourceProperty.PropertyType == targetProperty.PropertyType)
            {
                var targetParameterMember = Expression.Property(targetParameter, targetProperty);
                var sourceParameterMember = Expression.Property(sourceParameter, sourceProperty);
                var targetLambda = Expression.Lambda(targetParameterMember, targetParameter);
                var sourceLambda = Expression.Lambda(sourceParameterMember, sourceParameter);
                var mapperActionType = typeof(MapperActionMember<,,,>).MakeGenericType(
                    typeof(TTarget),
                    targetParameterMember.Type,
                    typeof(TSource),
                    sourceParameterMember.Type);

                var mapperAction = Activator.CreateInstance(mapperActionType, targetLambda, sourceLambda) as IMapperAction;

                if (descriptor.MapActions.Contains(mapperAction))
                {
                    throw new MapperInvalidMappingException(targetLambda);
                }

                descriptor.MapAction(mapperAction);
            }
        }

        return descriptor;
    }
}