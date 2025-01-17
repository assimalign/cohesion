using System;
using System.Reflection;
using System.Collections;
using System.Linq;
using System.Linq.Expressions;

namespace Assimalign.Cohesion.ObjectMapping.Internal;

using Assimalign.Cohesion.ObjectMapping.Properties;


/* 
 * This Mapper Action is for member to member mapping
 */
internal sealed class MapperActionMember<TTarget, TTargetMember, TSource, TSourceMember> : IMapperAction
{
    public MapperActionMember(Expression<Func<TTarget, TTargetMember>> target, Expression<Func<TSource, TSourceMember>> source)
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
        // Check if the source type can be assigned to the target type, if not throw an exception
        if (!typeof(TSourceMember).IsAssignableTo(typeof(TTargetMember)))
        {
            throw new InvalidCastException($"The source expression '{source}' cannot be assigned to the target expression '{target}'.");
        }

        SourceExpression = source;
        SourceGetter = source.Compile();
        TargetExpression = target;
        TargetMember = memberExpression.Member;
        TargetGetter = target.Compile();
    }


    public int Id => this.TargetType.GetHashCode() + TargetMember.GetHashCode();
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

        // This will ALWAYS allow 'Null' and 'Default' values
        if (context.IgnoreHandling == MapperIgnoreHandling.Never)
        {
            SetValue(target, sourceValue);
        }
        // This will NEVER allow 'Null' values (Defaults will be set if ValueType)
        else if (context.IgnoreHandling == MapperIgnoreHandling.Always && sourceValue is not null)
        {
            SetValue(target, sourceValue);
        }
        // This will NEITHER allow 'Null' or 'Default' values
        else if (context.IgnoreHandling == MapperIgnoreHandling.WhenMappingDefaults && !sourceValue.Equals(default(TSourceMember)))
        {
            SetValue(target, sourceValue);
        }
    }

    private TTargetMember GetTargetValue(TTarget target)
    {
        try
        {
            return TargetGetter.Invoke(target);
        }
        // Let's catch the exception for Null References only. This occurs when the Source Member Expression is chained and possibly null.
        catch (Exception exception) when (exception is NullReferenceException)
        {
            return default(TTargetMember);
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

