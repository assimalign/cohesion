using System;
using System.Linq;
using System.Linq.Expressions;

namespace Assimalign.Cohesion.ObjectMapping;

using Assimalign.Cohesion.ObjectMapping.Internal;
using System.Diagnostics.CodeAnalysis;

internal static class MapperUtility
{
    [RequiresUnreferencedCode("Calls System.Linq.Expressions.Expression.Property(Expression, String)")]
    public static MemberExpression GetMemberExpression(this ParameterExpression parameter, string memberName)
    {
        String[] paths = memberName.Split('.');
        Expression expression = parameter;

        for (int i = 0; i < paths.Length; i++)
        {
            expression = Expression.Property(expression, paths[i]);
        }

        return expression as MemberExpression;
    }
}

