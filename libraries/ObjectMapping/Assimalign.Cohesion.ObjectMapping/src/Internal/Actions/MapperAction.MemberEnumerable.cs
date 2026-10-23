using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Linq.Expressions;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.ObjectMapping.Internal;


/*
 * Maps an enumerable member by mapping each source element through the profiles registered for the
 * element types, then assigning the materialized collection to the target member. Reads and the
 * final (conversion + assign) write go through delegates that are either supplied directly (the
 * AOT-safe path emitted by the source generator, where the conversion is baked into the setter) or
 * compiled from expression trees with reflection-based conversion (the run-time fallback).
 */
[global::System.Diagnostics.DebuggerDisplay("MapperActionMemberEnumerable")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(MapperActionMemberEnumerable<,,,>.DebugView))]
internal sealed class MapperActionMemberEnumerable<TTarget, TTargetMember, TSource, TSourceMember> : TypedMapperAction<TTarget, TSource>
    where TSourceMember : class, new()
    where TTargetMember : class, new()
{
    private readonly Func<TSource, IEnumerable<TSourceMember>?> _sourceGetter;
    private readonly Func<TTarget, IEnumerable<TTargetMember>?> _targetGetter;
    private readonly Action<TTarget, IEnumerable<TTargetMember>> _setter;

    /// <summary>
    /// AOT-safe path: the getters and the conversion-aware setter are supplied directly
    /// (the shape the source generator emits).
    /// </summary>
    public MapperActionMemberEnumerable(
        Func<TSource, IEnumerable<TSourceMember>?> sourceGetter,
        Func<TTarget, IEnumerable<TTargetMember>?> targetGetter,
        Action<TTarget, IEnumerable<TTargetMember>> setter)
    {
        _sourceGetter = sourceGetter ?? throw new ArgumentNullException(nameof(sourceGetter));
        _targetGetter = targetGetter ?? throw new ArgumentNullException(nameof(targetGetter));
        _setter = setter ?? throw new ArgumentNullException(nameof(setter));
    }

    /// <summary>
    /// Run-time fallback: compiles the getters from expression trees and detects the target
    /// collection type via reflection. Not NativeAOT-safe.
    /// </summary>
    [RequiresDynamicCode("Compiles member-access expressions at run time; prefer a source-generated profile for AOT.")]
    public MapperActionMemberEnumerable(Expression<Func<TTarget, IEnumerable<TTargetMember>>> target, Expression<Func<TSource, IEnumerable<TSourceMember>>> source)
    {
        if (target.Body is not MemberExpression member)
        {
            throw new ArgumentException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.TargetExpressionMemberRequired, target));
        }
        if (member.Member.DeclaringType != typeof(TTarget))
        {
            throw new ArgumentException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.TargetMappingChainInvalid, target, typeof(TTarget).Name));
        }

        _sourceGetter = source.Compile();
        _targetGetter = target.Compile();

        var memberInfo = member.Member;
        var convert = CreateConverter(MemberType(memberInfo));
        _setter = (instance, items) => SetValue(memberInfo, instance!, convert(items));
    }

    public override void Invoke(IMapperContext context)
    {
        if (context.Source is not TSource source || context.Target is not TTarget target)
        {
            return;
        }

        var sourceValues = GetSourceValue(source);

        if (sourceValues is null) // No need to map the target if there is no data to map
        {
            return;
        }

        var matchingProfiles = context.Profiles
            .Where(p => p.TargetType == typeof(TTargetMember) && p.SourceType == typeof(TSourceMember))
            .ToList();

        var items = new List<TTargetMember>();

        // When merging, the existing target items are preserved ahead of the mapped items.
        if (context.CollectionHandling == MapperCollectionHandling.Merge)
        {
            var existing = GetTargetValue(target);

            if (existing is not null)
            {
                items.AddRange(existing);
            }
        }

        foreach (var sourceValue in sourceValues)
        {
            var targetValue = new TTargetMember();

            var elementContext = new MapperContext(targetValue, sourceValue)
            {
                Profiles = context.Profiles,
                CollectionHandling = context.CollectionHandling,
                IgnoreHandling = context.IgnoreHandling
            };

            foreach (var profile in matchingProfiles)
            {
                foreach (var action in profile.MapActions)
                {
                    action.Invoke(elementContext);
                }
            }

            items.Add(targetValue);
        }

        _setter.Invoke(target, items);
    }

    public override void Invoke(TTarget target, TSource source, ref MapperExecution execution)
    {
        var sourceValues = GetSourceValue(source);
        if (sourceValues is null)
        {
            return;
        }

        // Resolve once before enumeration, as on the public-context path. Callbacks may change profiles.
        var matchingProfiles = new List<IMapperProfile>();
        foreach (IMapperProfile profile in execution.Profiles)
        {
            if (profile.TargetType == typeof(TTargetMember) && profile.SourceType == typeof(TSourceMember))
            {
                matchingProfiles.Add(profile);
            }
        }
        var items = sourceValues is ICollection<TSourceMember> counted
            ? new List<TTargetMember>(counted.Count)
            : new List<TTargetMember>();
        if (execution.CollectionHandling == MapperCollectionHandling.Merge)
        {
            var existing = GetTargetValue(target);
            if (existing is not null)
            {
                items.AddRange(existing);
            }
        }

        if (sourceValues.GetType() == typeof(List<TSourceMember>))
        {
            var list = (List<TSourceMember>)sourceValues;
            foreach (var sourceValue in list)
            {
                items.Add(MapElement(sourceValue, matchingProfiles, ref execution));
            }
        }
        else if (sourceValues is TSourceMember[] array)
        {
            foreach (var sourceValue in array)
            {
                items.Add(MapElement(sourceValue, matchingProfiles, ref execution));
            }
        }
        else
        {
            foreach (var sourceValue in sourceValues)
            {
                items.Add(MapElement(sourceValue, matchingProfiles, ref execution));
            }
        }

        _setter.Invoke(target, items);
    }

    private static TTargetMember MapElement(TSourceMember sourceValue, List<IMapperProfile> profiles, ref MapperExecution execution)
    {
        var targetValue = new TTargetMember();
        var element = new MapperExecution(targetValue, sourceValue, execution.Profiles,
            execution.IgnoreHandling, execution.CollectionHandling);
        foreach (var profile in profiles)
        {
            element.Invoke(profile);
        }

        return targetValue;
    }

    private IEnumerable<TTargetMember>? GetTargetValue(TTarget target)
    {
        try
        {
            return _targetGetter.Invoke(target);
        }
        catch (NullReferenceException)
        {
            return null;
        }
    }

    private IEnumerable<TSourceMember>? GetSourceValue(TSource source)
    {
        try
        {
            return _sourceGetter.Invoke(source);
        }
        catch (NullReferenceException)
        {
            return null;
        }
    }

    private static Type MemberType(MemberInfo member) => member switch
    {
        PropertyInfo property => property.PropertyType,
        FieldInfo field => field.FieldType,
        _ => typeof(IEnumerable<TTargetMember>)
    };

    private static Func<IEnumerable<TTargetMember>, object> CreateConverter(Type memberType)
    {
        if (memberType.IsArray)
        {
            return enumerable => enumerable.ToArray();
        }
        if (memberType.IsAssignableFrom(typeof(List<TTargetMember>)))
        {
            return enumerable => enumerable.ToList();
        }
        if (memberType.IsAssignableFrom(typeof(HashSet<TTargetMember>)))
        {
            return enumerable => new HashSet<TTargetMember>(enumerable);
        }
        if (memberType.IsAssignableFrom(typeof(Queue<TTargetMember>)))
        {
            return enumerable => new Queue<TTargetMember>(enumerable);
        }
        if (memberType.IsAssignableFrom(typeof(Stack<TTargetMember>)))
        {
            return enumerable => new Stack<TTargetMember>(enumerable);
        }

        return enumerable => enumerable.ToList();
    }

    private static void SetValue(MemberInfo member, object instance, object value)
    {
        if (member is PropertyInfo property)
        {
            property.SetValue(instance, value);
        }
        else if (member is FieldInfo field)
        {
            field.SetValue(instance, value);
        }
    }

    private sealed class DebugView
    {
        public DebugView(MapperActionMemberEnumerable<TTarget, TTargetMember, TSource, TSourceMember> value)
        {
        }
    }
}
