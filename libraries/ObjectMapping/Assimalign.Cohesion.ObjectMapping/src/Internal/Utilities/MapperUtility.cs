using System;
using System.Collections.Generic;
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

    /// <summary>
    /// Extracts the ordered member path (root-to-leaf) from a member-access lambda such as
    /// <c>t =&gt; t.Info.FirstName</c>. Returns <see langword="null"/> when the body is not a pure
    /// member-access chain rooted at the lambda parameter.
    /// </summary>
    public static MemberInfo[]? ExtractMemberPath(LambdaExpression lambda)
    {
        var members = new List<MemberInfo>();
        Expression? current = Unwrap(lambda.Body);

        while (current is MemberExpression member)
        {
            members.Add(member.Member);
            current = Unwrap(member.Expression);
        }

        if (current is not ParameterExpression)
        {
            return null;
        }

        members.Reverse();
        return members.ToArray();

        static Expression? Unwrap(Expression? expression)
        {
            while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
            {
                expression = unary.Operand;
            }

            return expression;
        }
    }

    /// <summary>
    /// Compiles a setter for a (possibly nested) member path. Intermediate members that are
    /// <see langword="null"/> are created on demand, so <c>t =&gt; t.Info.FirstName</c> assigns into a
    /// freshly created <c>Info</c> when needed.
    /// </summary>
    /// <exception cref="MapperException">
    /// Thrown when an intermediate member is a value type or lacks a public parameterless
    /// constructor (so it cannot be created on demand).
    /// </exception>
    [UnconditionalSuppressMessage("Trimming", "IL2072",
        Justification = "Reached only from the [RequiresDynamicCode] expression mapping path; intermediate target types are user-provided POCOs expected to retain a parameterless constructor. Source-generated profiles use the trim-safe delegate path.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "Reached only from the [RequiresDynamicCode] expression mapping path; intermediate target types are user-provided POCOs expected to retain a parameterless constructor. Source-generated profiles use the trim-safe delegate path.")]
    public static Action<TInstance, TValue> CompilePathSetter<TInstance, TValue>(MemberInfo[] path)
    {
        var instance = Expression.Parameter(typeof(TInstance), "instance");
        var value = Expression.Parameter(typeof(TValue), "value");

        var statements = new List<Expression>();
        Expression current = instance;

        for (int i = 0; i < path.Length - 1; i++)
        {
            var memberType = MemberClrType(path[i]);

            if (memberType.IsValueType || memberType.GetConstructor(Type.EmptyTypes) is null)
            {
                throw new MapperException(
                    $"Cannot map into a nested target member: the intermediate type '{memberType.FullName}' must be a reference type with a public parameterless constructor.");
            }

            var access = Expression.MakeMemberAccess(current, path[i]);

            // if (instance.<member> is null) instance.<member> = new memberType();
            statements.Add(Expression.IfThen(
                Expression.ReferenceEqual(access, Expression.Constant(null, memberType)),
                Expression.Assign(Expression.MakeMemberAccess(current, path[i]), Expression.New(memberType))));

            current = Expression.MakeMemberAccess(current, path[i]);
        }

        var leaf = Expression.MakeMemberAccess(current, path[path.Length - 1]);
        Expression assigned = leaf.Type == typeof(TValue) ? value : Expression.Convert(value, leaf.Type);
        statements.Add(Expression.Assign(leaf, assigned));

        Expression body = statements.Count == 1 ? statements[0] : Expression.Block(statements);

        return Expression.Lambda<Action<TInstance, TValue>>(body, instance, value).Compile();
    }

    private static Type MemberClrType(MemberInfo member) => member switch
    {
        PropertyInfo property => property.PropertyType,
        FieldInfo field => field.FieldType,
        _ => throw new MapperException($"Unsupported member '{member.Name}'.")
    };
}
