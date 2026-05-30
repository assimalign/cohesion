using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace Assimalign.Cohesion.ObjectMapping;

using Assimalign.Cohesion.ObjectMapping.Internal;

public sealed class MapperProfileDescriptor<
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TTarget,
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TSource> 
{
    private readonly List<IMapperAction> _mapActions = new List<IMapperAction>();

    internal MapperProfileDescriptor(
        MapperProfile<TTarget, TSource> profile,
        List<IMapperAction> mapActions)
    {
        Profile = profile;
        _mapActions = mapActions;
    }

    /// <summary>
    /// The current profile being configured.
    /// </summary>
    public MapperProfile<TTarget, TSource> Profile { get; }

    /// <summary>
    /// Adds a mapping action to the profile.
    /// </summary>
    /// <param name="action"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public MapperProfileDescriptor<TTarget, TSource> MapAction(IMapperAction action) 
    {
        ArgumentNullException.ThrowIfNull(action);
        _mapActions.Add(action);
        return this;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public MapperProfileDescriptor<TTarget, TSource> MapAction(Action<IMapperContext> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return MapAction(new MapperAction(configure));
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public MapperProfileDescriptor<TTarget, TSource> MapAction(Action<TTarget, TSource> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return MapAction(new MapperAction<TTarget, TSource>(configure));
    }

    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="TTargetMember"></typeparam>
    /// <typeparam name="TSourceMember"></typeparam>
    /// <param name="target"></param>
    /// <param name="source"></param>
    /// <returns></returns>
    /// <exception cref="MapperException"></exception>
    public MapperProfileDescriptor<TTarget, TSource> MapMember<TTargetMember, TSourceMember>(
        Expression<Func<TTarget, TTargetMember>> target, 
        Expression<Func<TSource, TSourceMember>> source)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);

        return MapAction(new MapperActionMember<TTarget, TTargetMember, TSource, TSourceMember>(target, source));
    }

    /// <summary>
    /// Create a pointer map action for two complex members of 
    /// type <typeparamref name="TSource"/> and <typeparamref name="TTarget"/>.
    /// </summary>
    /// <typeparam name="TTargetMember"></typeparam>
    /// <typeparam name="TSourceMember"></typeparam>
    /// <param name="target"></param>
    /// <param name="source"></param>
    /// <returns></returns>
    public MapperProfileDescriptor<TTarget, TSource> MapMemberTypes<TTargetMember, TSourceMember>(
        Expression<Func<TTarget, TTargetMember>> target, 
        Expression<Func<TSource, TSourceMember>> source)
        where TTargetMember : class, new()
        where TSourceMember : class, new()
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);

        return MapAction(new MapperActionMemberType<TTarget, TTargetMember, TSource, TSourceMember>(target, source));
    }

    /// <summary>
    /// Create a pointer map action for two enumerable members of 
    /// type <typeparamref name="TSource"/> and <typeparamref name="TTarget"/>.
    /// </summary>
    /// <typeparam name="TTargetMember"></typeparam>
    /// <typeparam name="TSourceMember"></typeparam>
    /// <param name="target"></param>
    /// <param name="source"></param>
    /// <returns></returns>
    /// <exception cref="Exception"></exception>
    /// <exception cref="MapperInvalidMappingException"></exception>
    public MapperProfileDescriptor<TTarget, TSource> MapMemberEnumerables<TTargetMember, TSourceMember>(
        Expression<Func<TTarget, IEnumerable<TTargetMember>>> target, 
        Expression<Func<TSource, IEnumerable<TSourceMember>>> source)
        where TTargetMember : class, new()
        where TSourceMember : class, new()
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);

        return MapAction(new MapperActionMemberEnumerable<TTarget, TTargetMember, TSource, TSourceMember>(target, source));
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

    [RequiresDynamicCode("")]
    [RequiresUnreferencedCode("")]
    public MapperProfileDescriptor<TTarget, TSource> MapMember(string target, string source)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(target);
        ArgumentNullException.ThrowIfNullOrEmpty(source);


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

        IMapperAction mapperAction = (IMapperAction)Activator.CreateInstance(mapperActionType, targetLambda, sourceLambda)!;

        return MapAction(mapperAction);
    }


    /// <summary>
    /// Tries to map all Members of <typeparamref name="TTarget"/> and <typeparamref name="TSource"/> that share the same name.
    /// </summary>
    /// <returns></returns>
    [RequiresDynamicCode("")]
    [RequiresUnreferencedCode("")]
    public MapperProfileDescriptor<TTarget, TSource> MapAllMembers()
    {
        return MapAllProperties().MapAllFields();
    }

    /// <summary>
    /// Tries to map only Field Members of <typeparamref name="TTarget"/> and <typeparamref name="TSource"/> that share the same name.
    /// </summary>
    /// <returns></returns>
    [RequiresDynamicCode("")]
    [RequiresUnreferencedCode("")]
    public MapperProfileDescriptor<TTarget, TSource> MapAllFields()
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

                var mapperAction = (IMapperAction)Activator.CreateInstance(mapperActionType, targetLambda, sourceLambda)!;

                MapAction(mapperAction);
            }
        }

        return this;
    }

    /// <summary>
    /// Tries to map only Property Members of <typeparamref name="TTarget"/> and <typeparamref name="TSource"/> that share the same name.
    /// </summary>
    /// <returns></returns>
    [RequiresDynamicCode("")]
    [RequiresUnreferencedCode("")]
    public MapperProfileDescriptor<TTarget, TSource> MapAllProperties()
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

                var mapperAction = (IMapperAction)Activator.CreateInstance(mapperActionType, targetLambda, sourceLambda)!;

                MapAction(mapperAction);
            }
        }

        return this;
    }
}