using System;
using System.Reflection;
using System.Linq.Expressions;
using System.Collections.Generic;
using System.Linq;

namespace Assimalign.Cohesion.ObjectMapping.Internal;

using Assimalign.Cohesion.ObjectMapping.Properties;
using Assimalign.Cohesion.ObjectMapping.Internal.Exceptions;

internal sealed class MapperActionNestedProfile<TTarget, TTargetMember, TSource, TSourceMember> : IMapperAction
    where TTargetMember : class, new()
    where TSourceMember : class, new()
{

    public MapperActionNestedProfile(Expression<Func<TTarget, TTargetMember>> target, Expression<Func<TSource, TSourceMember>> source)
    {
        if (target.Body is not MemberExpression member)
        {
            throw new ArgumentException($"The target expression body: '{target}' must be a MemberExpression.");
        }
        if (member.Member.DeclaringType != typeof(TTarget))
        {
            throw new Exception(string.Format(Resources.MapperExceptionInvalidChaining, target, typeof(TTarget).Name));
        }

        SourceExpression = source;
        SourceGetter = source.Compile();
        TargetExpression = target;
        TargetMember = member.Member;
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
    // To prevent searching a profile in the Mapper options lets just store the reference in a property.
    public IMapperProfile Profile { get; init; }

    public void Invoke(IMapperContext context)
    {
        if (context.Source is not TSource source)
        {
            return;
            //throw new MapperInvalidContextException(context.Source.GetType(), context.Target.GetType(), typeof(TSource), typeof(TTarget));
        }
        if (context.Target is not TTarget target)
        {
            return;
            //throw new MapperInvalidContextException(context.Source.GetType(), context.Target.GetType(), typeof(TSource), typeof(TTarget));
        }

        var targetValue = GetTargetValue(target);
        var sourceValue = GetSourceValue(source);

        if (context is MapperContext ictx)
        {
            //if (context.IgnoreHandling == MapperIgnoreHandling.Never && sourceValue is null)
            //{
            //    SetValue(target, null);
            //}
            //if (context.IgnoreHandling == MapperIgnoreHandling.Always && sourceValue is not null)
            //{
            //    var ncontext = new MapperContext(targetValue, sourceValue)
            //    {
            //        MapOptions = ictx.MapOptions
            //    };

            //    foreach (var action in Profile.MapActions)
            //    {
            //        action.Invoke(ncontext);
            //    }

            //    SetValue(target, targetValue);
            //}
        }
    }

    private TSourceMember GetSourceValue(TSource source)
    {
        try
        {
            return SourceGetter.Invoke(source);
        }
        catch (Exception exception) when (exception is NullReferenceException)
        {
            return default;
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
}