using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace Assimalign.Cohesion.ObjectMapping;

using Assimalign.Cohesion.ObjectMapping.Internal;

/// <summary>
/// The fluent configuration surface for a <see cref="MapperProfile{TTarget, TSource}"/>.
/// Each method records a mapping action that runs when the profile is applied.
/// </summary>
/// <typeparam name="TTarget">The target type the profile maps to.</typeparam>
/// <typeparam name="TSource">The source type the profile maps from.</typeparam>
public sealed class MapperProfileDescriptor<
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TTarget,
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TSource>
{
    private readonly List<IMapperAction> _mapActions;

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
    /// <param name="action">The action to add.</param>
    /// <returns>The same descriptor for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="action"/> is <see langword="null"/>.</exception>
    public MapperProfileDescriptor<TTarget, TSource> MapAction(IMapperAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _mapActions.Add(action);
        return this;
    }

    /// <summary>
    /// Adds a custom mapping action that operates directly on the mapping context.
    /// </summary>
    /// <param name="configure">The callback invoked with the mapping context.</param>
    /// <returns>The same descriptor for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="configure"/> is <see langword="null"/>.</exception>
    public MapperProfileDescriptor<TTarget, TSource> MapAction(Action<IMapperContext> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return MapAction(new MapperAction(configure));
    }

    /// <summary>
    /// Adds a custom mapping action that operates on the strongly typed target and source.
    /// </summary>
    /// <param name="configure">The callback invoked with the target and source instances.</param>
    /// <returns>The same descriptor for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="configure"/> is <see langword="null"/>.</exception>
    public MapperProfileDescriptor<TTarget, TSource> MapAction(Action<TTarget, TSource> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return MapAction(new MapperAction<TTarget, TSource>(configure));
    }

    /// <summary>
    /// Maps a scalar member from the source to a writable member of the target.
    /// </summary>
    /// <typeparam name="TTargetMember">The target member type.</typeparam>
    /// <typeparam name="TSourceMember">The source member type. Must be assignable to <typeparamref name="TTargetMember"/>.</typeparam>
    /// <param name="target">An expression selecting the target member.</param>
    /// <param name="source">An expression producing the source value.</param>
    /// <returns>The same descriptor for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="target"/> or <paramref name="source"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when the target expression is not a member of <typeparamref name="TTarget"/>.</exception>
    /// <exception cref="InvalidCastException">Thrown when <typeparamref name="TSourceMember"/> is not assignable to <typeparamref name="TTargetMember"/>.</exception>
    [RequiresDynamicCode("Compiles member-access expressions at run time; prefer a source-generated profile for AOT.")]
    public MapperProfileDescriptor<TTarget, TSource> MapMember<TTargetMember, TSourceMember>(
        Expression<Func<TTarget, TTargetMember>> target,
        Expression<Func<TSource, TSourceMember>> source)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);

        return MapAction(new MapperActionMember<TTarget, TTargetMember, TSource, TSourceMember>(target, source));
    }

    /// <summary>
    /// Registers a scalar member mapping from pre-built getter and setter delegates.
    /// This is the AOT-safe form emitted by the source generator: no expression compilation occurs.
    /// The target member type is captured by the <paramref name="setter"/>, so only the source
    /// value type is a type parameter (and is inferred).
    /// </summary>
    /// <typeparam name="TSourceMember">The source value type.</typeparam>
    /// <param name="getter">Reads the value from the source.</param>
    /// <param name="setter">Writes the value to the target member.</param>
    /// <returns>The same descriptor for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="getter"/> or <paramref name="setter"/> is <see langword="null"/>.</exception>
    public MapperProfileDescriptor<TTarget, TSource> MapMember<TSourceMember>(
        Func<TSource, TSourceMember> getter,
        Action<TTarget, TSourceMember> setter)
    {
        ArgumentNullException.ThrowIfNull(getter);
        ArgumentNullException.ThrowIfNull(setter);

        return MapAction(new MapperActionMember<TTarget, TSourceMember, TSource, TSourceMember>(getter, setter));
    }

    /// <summary>
    /// Maps a complex (reference) member by delegating to the profile registered
    /// for the <typeparamref name="TTargetMember"/>/<typeparamref name="TSourceMember"/> pair.
    /// </summary>
    /// <typeparam name="TTargetMember">The target member type.</typeparam>
    /// <typeparam name="TSourceMember">The source member type.</typeparam>
    /// <param name="target">An expression selecting the target member.</param>
    /// <param name="source">An expression selecting the source member.</param>
    /// <returns>The same descriptor for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="target"/> or <paramref name="source"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when the target expression is not a member of <typeparamref name="TTarget"/>.</exception>
    [RequiresDynamicCode("Compiles member-access expressions at run time; prefer a source-generated profile for AOT.")]
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
    /// Maps a complex (reference) member from pre-built read/write delegates, delegating to the
    /// profile registered for the <typeparamref name="TTargetMember"/>/<typeparamref name="TSourceMember"/>
    /// pair. This is the AOT-safe form emitted by the source generator.
    /// </summary>
    /// <typeparam name="TTargetMember">The target member type.</typeparam>
    /// <typeparam name="TSourceMember">The source member type.</typeparam>
    /// <param name="sourceGetter">Reads the source member.</param>
    /// <param name="targetGetter">Reads the existing target member (reused if present).</param>
    /// <param name="setter">Writes the target member.</param>
    /// <returns>The same descriptor for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when any delegate is <see langword="null"/>.</exception>
    public MapperProfileDescriptor<TTarget, TSource> MapMemberTypes<TTargetMember, TSourceMember>(
        Func<TSource, TSourceMember?> sourceGetter,
        Func<TTarget, TTargetMember?> targetGetter,
        Action<TTarget, TTargetMember?> setter)
        where TTargetMember : class, new()
        where TSourceMember : class, new()
    {
        ArgumentNullException.ThrowIfNull(sourceGetter);
        ArgumentNullException.ThrowIfNull(targetGetter);
        ArgumentNullException.ThrowIfNull(setter);

        return MapAction(new MapperActionMemberType<TTarget, TTargetMember, TSource, TSourceMember>(sourceGetter, targetGetter, setter));
    }

    /// <summary>
    /// Maps an enumerable member by mapping each source element through the profile
    /// registered for the <typeparamref name="TTargetMember"/>/<typeparamref name="TSourceMember"/> pair.
    /// </summary>
    /// <typeparam name="TTargetMember">The target element type.</typeparam>
    /// <typeparam name="TSourceMember">The source element type.</typeparam>
    /// <param name="target">An expression selecting the target enumerable member.</param>
    /// <param name="source">An expression selecting the source enumerable member.</param>
    /// <returns>The same descriptor for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="target"/> or <paramref name="source"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when the target expression is not a member of <typeparamref name="TTarget"/>.</exception>
    [RequiresDynamicCode("Compiles member-access expressions at run time; prefer a source-generated profile for AOT.")]
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
    /// Maps an enumerable member from pre-built read/write delegates. The <paramref name="setter"/>
    /// is responsible for materializing the mapped sequence into the target's concrete collection
    /// type. This is the AOT-safe form emitted by the source generator.
    /// </summary>
    /// <typeparam name="TTargetMember">The target element type.</typeparam>
    /// <typeparam name="TSourceMember">The source element type.</typeparam>
    /// <param name="sourceGetter">Reads the source sequence.</param>
    /// <param name="targetGetter">Reads the existing target sequence (used when merging).</param>
    /// <param name="setter">Materializes and assigns the mapped sequence to the target member.</param>
    /// <returns>The same descriptor for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when any delegate is <see langword="null"/>.</exception>
    public MapperProfileDescriptor<TTarget, TSource> MapMemberEnumerables<TTargetMember, TSourceMember>(
        Func<TSource, IEnumerable<TSourceMember>?> sourceGetter,
        Func<TTarget, IEnumerable<TTargetMember>?> targetGetter,
        Action<TTarget, IEnumerable<TTargetMember>> setter)
        where TTargetMember : class, new()
        where TSourceMember : class, new()
    {
        ArgumentNullException.ThrowIfNull(sourceGetter);
        ArgumentNullException.ThrowIfNull(targetGetter);
        ArgumentNullException.ThrowIfNull(setter);

        return MapAction(new MapperActionMemberEnumerable<TTarget, TTargetMember, TSource, TSourceMember>(sourceGetter, targetGetter, setter));
    }


    /// <summary>
    /// Maps a member by string name (dotted paths supported on the source) from the
    /// <typeparamref name="TSource"/> type to the <typeparamref name="TTarget"/> type.
    /// </summary>
    /// <param name="target">The property name within the <typeparamref name="TTarget"/>.</param>
    /// <param name="source">The property name within the <typeparamref name="TSource"/>.</param>
    /// <returns>The same descriptor for chaining.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="target"/> or <paramref name="source"/> is <see langword="null"/> or empty.</exception>
    [RequiresDynamicCode("Builds and compiles member access expressions at runtime.")]
    [RequiresUnreferencedCode("Resolves members by name via reflection.")]
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
    /// Tries to map all members (properties and fields) of <typeparamref name="TTarget"/> and
    /// <typeparamref name="TSource"/> that share the same name and type.
    /// </summary>
    /// <returns>The same descriptor for chaining.</returns>
    [RequiresDynamicCode("Builds and compiles member access expressions at runtime.")]
    [RequiresUnreferencedCode("Enumerates members via reflection.")]
    public MapperProfileDescriptor<TTarget, TSource> MapAllMembers()
    {
        return MapAllProperties().MapAllFields();
    }

    /// <summary>
    /// Tries to map only the field members of <typeparamref name="TTarget"/> and
    /// <typeparamref name="TSource"/> that share the same name and type.
    /// </summary>
    /// <returns>The same descriptor for chaining.</returns>
    [RequiresDynamicCode("Builds and compiles member access expressions at runtime.")]
    [RequiresUnreferencedCode("Enumerates members via reflection.")]
    public MapperProfileDescriptor<TTarget, TSource> MapAllFields()
    {
        var targetType = typeof(TTarget);
        var sourceType = typeof(TSource);

        var targetParameter = Expression.Parameter(targetType);
        var sourceParameter = Expression.Parameter(sourceType);

        foreach (var targetField in targetType.GetFields().Where(x => x.IsPublic && !x.IsInitOnly && !x.IsLiteral))
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
    /// Tries to map only the property members of <typeparamref name="TTarget"/> and
    /// <typeparamref name="TSource"/> that share the same name and type.
    /// </summary>
    /// <returns>The same descriptor for chaining.</returns>
    [RequiresDynamicCode("Builds and compiles member access expressions at runtime.")]
    [RequiresUnreferencedCode("Enumerates members via reflection.")]
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
