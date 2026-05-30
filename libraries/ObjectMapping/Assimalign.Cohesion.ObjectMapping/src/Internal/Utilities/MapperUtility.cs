using System;
using System.Linq.Expressions;
using System.Reflection;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.ObjectMapping.Internal;

internal static class MapperUtility
{
    [RequiresUnreferencedCode("Calls System.Linq.Expressions.Expression.Property(Expression, String)")]
    public static MemberExpression GetMemberExpression(this ParameterExpression parameter, string memberName)
    {
        string[] paths = memberName.Split('.');
        Expression expression = parameter;

        for (int i = 0; i < paths.Length; i++)
        {
            expression = Expression.Property(expression, paths[i]);
        }

        return (expression as MemberExpression)!;
    }

    /// <summary>
    /// Compiles a strongly typed setter delegate for a property or field, used on
    /// the write hot path instead of reflection. When the supplied value type does
    /// not match the member type exactly an assignability-checked conversion is
    /// inserted.
    /// </summary>
    public static Action<TInstance, TValue> CompileSetter<TInstance, TValue>(MemberInfo member)
    {
        var instance = Expression.Parameter(typeof(TInstance), "instance");
        var value = Expression.Parameter(typeof(TValue), "value");
        var access = Expression.MakeMemberAccess(instance, member);

        Expression assigned = access.Type == typeof(TValue)
            ? value
            : Expression.Convert(value, access.Type);

        var body = Expression.Assign(access, assigned);

        return Expression.Lambda<Action<TInstance, TValue>>(body, instance, value).Compile();
    }
}
