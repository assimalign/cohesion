using System;
using System.Linq;
using System.Reflection;
using System.Linq.Expressions;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ObjectMapping.Internal;

using Assimalign.Cohesion.ObjectMapping.Properties;
using Assimalign.Cohesion.ObjectMapping.Internal.Exceptions;

internal sealed class MapperActionMemberEnumerable<TTarget, TTargetMember, TSource, TSourceMember> : IMapperAction
    where TSourceMember : class, new()
    where TTargetMember : class, new()
{
    private Func<IEnumerable<TTargetMember>, IEnumerable<TTargetMember>> convert;

    public MapperActionMemberEnumerable(Expression<Func<TTarget, IEnumerable<TTargetMember>>> target, Expression<Func<TSource, IEnumerable<TSourceMember>>> source)
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

        SetEnumerableConverter();
    }

    private void SetEnumerableConverter()
    {
        if (typeof(List<TTargetMember>).IsAssignableTo(TargetExpression.ReturnType))
        {
            convert = enumerable => enumerable.ToList();
        }
        else if (typeof(TSourceMember[]).IsAssignableTo(TargetExpression.ReturnType))
        {
            convert = enumerable => enumerable.ToArray();
        }
        else if (typeof(Queue<TSourceMember>).IsAssignableTo(TargetExpression.ReturnType))
        {
            convert = enumerable => new Queue<TTargetMember>(enumerable.AsEnumerable());
        }
        else if (typeof(Stack<TSourceMember>).IsAssignableTo(TargetExpression.ReturnType))
        {
            convert = enumerable => new Stack<TTargetMember>(enumerable.AsEnumerable());
        }
        else if (typeof(IEnumerable<TSourceMember>).IsAssignableTo(TargetExpression.ReturnType))
        {
            convert = enumerable => enumerable.AsEnumerable();
        }
        else
        {
            convert = enumerable => enumerable;
        }
    }

    public int Id => this.TargetType.GetHashCode() + TargetMember.GetHashCode();
    public Type TargetType => typeof(TTarget);
    public MemberInfo TargetMember { get; }
    public Func<TTarget, IEnumerable<TTargetMember>> TargetGetter { get; }
    public Expression<Func<TTarget, IEnumerable<TTargetMember>>> TargetExpression { get; }

    public Type SourceType => typeof(TSource);
    public Func<TSource, IEnumerable<TSourceMember>> SourceGetter { get; }
    public Expression<Func<TSource, IEnumerable<TSourceMember>>> SourceExpression { get; }

    public void Invoke(IMapperContext context)
    {
        if (context.Source is not TSource source || context.Target is not TTarget target)
        {
            return;
        }

        var sourceValues = GetSourceValue(source);
        var targetValues = GetTargetValue(target);

        if (sourceValues is not null) // No need to try mapping target if there is no data to map
        {
            var items = context.Profiles
                .Where(p => p.TargetType == typeof(TTargetMember) && p.SourceType == typeof(TSourceMember))
                .SelectMany(profile =>
                {
                    var targetValues = new List<TTargetMember>();

                    foreach (var sourceValue in sourceValues)
                    {
                        var targetValue = new TTargetMember();

                        foreach (var action in profile.MapActions)
                        {
                            action.Invoke(new MapperContext(targetValue, sourceValue)
                            {
                                Profiles = context.Profiles,
                                CollectionHandling = context.CollectionHandling,
                                IgnoreHandling = context.IgnoreHandling
                            });
                        }

                        targetValues.Add(targetValue);
                    }

                    return targetValues;
                })
                .ToList();

            // Let's merge the collection if applicable
            if (context.CollectionHandling == MapperCollectionHandling.Merge && targetValues is not null)
            {
                foreach (var item in targetValues)
                {
                    items.Add(item);
                }
            }

            SetValue(target, convert.Invoke(items));
        }
    }
    private IEnumerable<TTargetMember> GetTargetValue(TTarget target)
    {
        try
        {
            return TargetGetter.Invoke(target);
        }
        // Let's catch the exception for Null References only. This occurs when the Source Member Expression is chained and possibly null.
        catch (Exception exception) when (exception is NullReferenceException)
        {
            return default(IEnumerable<TTargetMember>);
        }
    }
    private IEnumerable<TSourceMember> GetSourceValue(TSource source)
    {
        try
        {
            return SourceGetter.Invoke(source);
        }
        catch (Exception exception) when (exception is NullReferenceException)
        {
            return null;
        }
    }


    private void SetValue(object instance, object value)
    {
        if (TargetMember is PropertyInfo property)
        {
            property.SetValue(instance, value);
        }
        else if (TargetMember is FieldInfo field)
        {
            field.SetValue(instance, value);
        }
    }
}