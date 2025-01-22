using System;
using System.Reflection;
using System.Collections;
using System.Linq;
using System.Linq.Expressions;

namespace Assimalign.Cohesion.ObjectMapping.Internal;

using Assimalign.Cohesion.ObjectMapping.Properties;

internal sealed class MapperActionMemberType<TTarget, TTargetMember, TSource, TSourceMember> : IMapperAction
    where TTargetMember : class, new()
    where TSourceMember : class, new()
{
    public MapperActionMemberType(Expression<Func<TTarget, TTargetMember>> target, Expression<Func<TSource, TSourceMember>> source)
    {
        if (target.Body is not MemberExpression memberExpression)
        {
            throw new ArgumentException($"The target expression body: '{target}' must be a MemberExpression.");
        }
        // Ensure that the member is of type TTarget (Target Members cannot be nested.)
        if (memberExpression.Member.DeclaringType != typeof(TTarget))
        {
            throw new Exception(string.Format(Resources.MapperExceptionInvalidChaining, target, typeof(TTarget).Name));
        }
        SourceExpression = source;
        SourceGetter = source.Compile();
        TargetExpression = target;
        TargetMember = memberExpression.Member;
        TargetGetter = target.Compile();
    }


    public int Id => this.TargetType.GetHashCode();
    public Type TargetType => typeof(TTarget);
    public MemberInfo TargetMember { get; }
    public Func<TTarget, TTargetMember> TargetGetter { get; }
    public Expression<Func<TTarget, TTargetMember>> TargetExpression { get; }
    public Type SourceType => typeof(TSource);
    public Func<TSource, TSourceMember> SourceGetter { get; }
    public Expression<Func<TSource, TSourceMember>> SourceExpression { get; }

    public void Invoke(IMapperContext context)
    {
        if (context.Source is not TSource source || context.Target is not TTarget target)
        {
            return;
        }

        var sourceValue = GetSourceValue(source);
        var targetValue = GetTargetValue(target);

        var profiles = context.Profiles
            .Where(p => p.TargetType == typeof(TTargetMember) && p.SourceType == typeof(TSourceMember));

        foreach (var profile in profiles)
        {
            foreach (var action in profile.MapActions)
            {
                action.Invoke(new MapperContext(targetValue, sourceValue)
                {
                    Profiles = context.Profiles,
                    CollectionHandling = context.CollectionHandling,
                    IgnoreHandling = context.IgnoreHandling
                });
            }
        }

        // This will ALWAYS allow 'Null' and 'Default' values
        if (context.IgnoreHandling == MapperIgnoreHandling.Never)
        {
            SetValue(target, targetValue);
        }
        // This will NEVER allow 'Null' values (Defaults will be set if ValueType)
        else if (context.IgnoreHandling == MapperIgnoreHandling.Always && sourceValue is not null)
        {
            SetValue(target, targetValue);
        }
        // This will NEITHER allow 'Null' or 'Default' values
        else if (context.IgnoreHandling == MapperIgnoreHandling.WhenMappingDefaults && !sourceValue.Equals(default(TSourceMember)))
        {
            SetValue(target, targetValue);
        }
    }

    private TTargetMember GetTargetValue(TTarget target)
    {
        try
        {
            return TargetGetter.Invoke(target) ?? new TTargetMember();
        }
        // Let's catch the exception for Null References only. This occurs when the Source Member Expression is chained and possibly null.
        catch (Exception exception) when (exception is NullReferenceException)
        {
            return new TTargetMember();
        }
    }
    private TSourceMember GetSourceValue(TSource source)
    {
        try
        {
            return SourceGetter.Invoke(source);
        }
        // Let's catch the exception for Null References only. This occurs when the Source Member Expression is chained and possibly null.
        catch (Exception exception) when (exception is NullReferenceException)
        {
            return default(TSourceMember);
        }
    }
    private void SetValue(object targetInstance, object targetValue)
    {
        switch (TargetMember)
        {
            case PropertyInfo property:
                {
                    property.SetValue(targetInstance, targetValue);
                    break;
                }
            case FieldInfo field:
                {
                    field.SetValue(targetInstance, targetValue);
                    break;
                }
            default:
                {
                    // This should never hit, but added just encase
                    throw new NotSupportedException($"The Target Member  of expression '{TargetExpression}' is not supported. Unknown System.Reflection.MemberInfo.");
                }
        }
    }

    public override bool Equals(object instance) => instance is IMapperAction action ? action.Id == this.Id : false;
    public override int GetHashCode() => HashCode.Combine(TargetType, TargetMember);
}
