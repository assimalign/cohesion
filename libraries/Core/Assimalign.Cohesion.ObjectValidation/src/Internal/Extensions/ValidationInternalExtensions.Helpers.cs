using System;
using System.Linq;
using System.Linq.Expressions;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Assimalign.Cohesion.ObjectValidation.Internal;


internal static partial class ValidationInternalExtensions
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string GetExpressionBody<T, TValue>(this IValidationItem<T, TValue> item)
    {
        if (item.ItemExpression.Body is MemberExpression member)
        {
            return string.Join('.', member.ToString().Split('.').Skip(1));
        }

        return null;
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string GetExpressionBody<T, TValue>(this IValidationItem item)
    {
        if (item is IValidationItem<T, TValue> validationItem)
        {
            return validationItem.GetExpressionBody();
        }

        return null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Func<TIn,TOut> Memoise<TIn,TOut>(Func<TIn, TOut> method)
    {
        var cache = new Dictionary<TIn, TOut>();

        return input => cache.TryGetValue(input, out var result) ? 
            result : 
            method.Invoke(input);
    }
}

