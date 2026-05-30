using System;
using System.Linq.Expressions;
using System.Reflection;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ObjectMapping.Internal;

using Assimalign.Cohesion.ObjectMapping.Properties;

/*
 * Maps a complex (reference) member by delegating to the profiles registered for the member's
 * target/source types, against a child context scoped to the member instances. Reads and writes
 * go through delegates that are either supplied directly (the AOT-safe path emitted by the source
 * generator) or compiled from expression trees (the run-time fallback).
 */
internal sealed class MapperActionMemberType<TTarget, TTargetMember, TSource, TSourceMember> : IMapperAction
    where TTargetMember : class, new()
    where TSourceMember : class, new()
{
    private readonly Func<TSource, TSourceMember?> _sourceGetter;
    private readonly Func<TTarget, TTargetMember?> _targetGetter;
    private readonly Action<TTarget, TTargetMember?> _setter;

    /// <summary>
    /// AOT-safe path: the getters and setter are supplied directly (the shape the source generator emits).
    /// </summary>
    public MapperActionMemberType(
        Func<TSource, TSourceMember?> sourceGetter,
        Func<TTarget, TTargetMember?> targetGetter,
        Action<TTarget, TTargetMember?> setter)
    {
        _sourceGetter = sourceGetter ?? throw new ArgumentNullException(nameof(sourceGetter));
        _targetGetter = targetGetter ?? throw new ArgumentNullException(nameof(targetGetter));
        _setter = setter ?? throw new ArgumentNullException(nameof(setter));
    }

    /// <summary>
    /// Run-time fallback: compiles the getters and setter from expression trees. Not NativeAOT-safe.
    /// </summary>
    [RequiresDynamicCode("Compiles member-access expressions at run time; prefer a source-generated profile for AOT.")]
    public MapperActionMemberType(Expression<Func<TTarget, TTargetMember>> target, Expression<Func<TSource, TSourceMember>> source)
    {
        if (target.Body is not MemberExpression memberExpression)
        {
            throw new ArgumentException($"The target expression body: '{target}' must be a MemberExpression.");
        }
        // Ensure that the member is of type TTarget (Target Members cannot be nested.)
        if (memberExpression.Member.DeclaringType != typeof(TTarget))
        {
            throw new ArgumentException(string.Format(Resources.MapperExceptionInvalidChaining, target, typeof(TTarget).Name));
        }

        _sourceGetter = source.Compile();
        _targetGetter = target.Compile();
        _setter = MapperUtility.CompileSetter<TTarget, TTargetMember?>(memberExpression.Member);
    }

    public void Invoke(IMapperContext context)
    {
        if (context.Source is not TSource source || context.Target is not TTarget target)
        {
            return;
        }

        var sourceValue = GetSourceValue(source);

        // A null nested source is treated like a null scalar: only written when nulls are allowed.
        if (sourceValue is null)
        {
            if (context.IgnoreHandling == MapperIgnoreHandling.Never)
            {
                _setter.Invoke(target, null);
            }

            return;
        }

        var targetValue = GetTargetValue(target);

        var nestedContext = new MapperContext(targetValue, sourceValue)
        {
            Profiles = context.Profiles,
            IgnoreHandling = context.IgnoreHandling,
            CollectionHandling = context.CollectionHandling
        };

        IReadOnlyList<IMapperProfile> profiles = context.Profiles;

        for (int i = 0; i < profiles.Count; i++)
        {
            IMapperProfile profile = profiles[i];

            if (profile.IsMatch(typeof(TTargetMember), typeof(TSourceMember)))
            {
                foreach (IMapperAction action in profile.MapActions)
                {
                    action.Invoke(nestedContext);
                }
            }
        }

        _setter.Invoke(target, targetValue);
    }

    private TSourceMember? GetSourceValue(TSource source)
    {
        try
        {
            return _sourceGetter.Invoke(source);
        }
        // Catch NullReferenceException only; occurs when a chained source member is null.
        catch (NullReferenceException)
        {
            return default;
        }
    }

    private TTargetMember GetTargetValue(TTarget target)
    {
        try
        {
            return _targetGetter.Invoke(target) ?? new TTargetMember();
        }
        // Catch NullReferenceException only; occurs when a chained target member is null.
        catch (NullReferenceException)
        {
            return new TTargetMember();
        }
    }
}
