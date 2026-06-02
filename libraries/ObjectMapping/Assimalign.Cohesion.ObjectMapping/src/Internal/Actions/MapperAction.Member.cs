using System;
using System.Linq.Expressions;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.ObjectMapping.Internal;


/*
 * Scalar member-to-member mapping. Reads through a getter and writes through a
 * setter. The delegates can be supplied directly (the AOT-safe path, used by the
 * source generator) or derived from expression trees at run time (the fallback
 * path, which is not NativeAOT-safe and is annotated accordingly).
 */
internal sealed class MapperActionMember<TTarget, TTargetMember, TSource, TSourceMember> : IMapperAction
{
    private readonly Func<TSource, TSourceMember> _getter;
    private readonly Action<TTarget, TSourceMember> _setter;

    /// <summary>
    /// AOT-safe path: the getter and setter are supplied directly. This is the shape the
    /// source generator emits — no expression compilation occurs.
    /// </summary>
    public MapperActionMember(Func<TSource, TSourceMember> getter, Action<TTarget, TSourceMember> setter)
    {
        _getter = getter ?? throw new ArgumentNullException(nameof(getter));
        _setter = setter ?? throw new ArgumentNullException(nameof(setter));
    }

    /// <summary>
    /// Runtime fallback: compiles a getter and a setter from expression trees. Not
    /// NativeAOT-safe (relies on <see cref="Expression{T}.Compile()"/>).
    /// </summary>
    [RequiresDynamicCode("Compiles member-access expressions at run time; prefer a source-generated profile for AOT.")]
    public MapperActionMember(Expression<Func<TTarget, TTargetMember>> target, Expression<Func<TSource, TSourceMember>> source)
    {
        if (target.Body is not MemberExpression)
        {
            throw new ArgumentException($"The target expression body: '{target}' must be a MemberExpression.");
        }
        // The target may be a member path (e.g. t => t.Info.FirstName); intermediate members are
        // created on demand by the compiled setter.
        if (MapperUtility.ExtractMemberPath(target) is not { Length: > 0 } path)
        {
            throw new ArgumentException($"The target expression '{target}' must be a member access path on the parameter (e.g. 't => t.A.B').");
        }
        // Check if the source type can be assigned to the target (leaf) type, if not throw an exception
        if (!typeof(TSourceMember).IsAssignableTo(typeof(TTargetMember)))
        {
            throw new InvalidCastException($"The source expression '{source}' cannot be assigned to the target expression '{target}'.");
        }

        _getter = source.Compile();
        _setter = MapperUtility.CompilePathSetter<TTarget, TSourceMember>(path);
    }

    public void Invoke(IMapperContext context)
    {
        if (context.Source is not TSource source || context.Target is not TTarget target)
        {
            return;
        }

        var sourceValue = GetSourceValue(source);

        switch (context.IgnoreHandling)
        {
            // This will ALWAYS allow 'Null' and 'Default' values
            case MapperIgnoreHandling.Never:
                _setter.Invoke(target, sourceValue);
                break;

            // This will NEVER allow 'Null' values (Defaults will be set if ValueType)
            case MapperIgnoreHandling.Always when sourceValue is not null:
                _setter.Invoke(target, sourceValue);
                break;

            // This will NEITHER allow 'Null' nor 'Default' values
            case MapperIgnoreHandling.WhenMappingDefaults when sourceValue is not null && !sourceValue.Equals(default(TSourceMember)):
                _setter.Invoke(target, sourceValue);
                break;
        }
    }

    private TSourceMember GetSourceValue(TSource source)
    {
        try
        {
            return _getter.Invoke(source);
        }
        // Let's catch the exception for Null References only. This occurs when the Source Member Expression is chained and possibly null.
        catch (NullReferenceException)
        {
            return default!;
        }
    }
}
