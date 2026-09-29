using System;
using System.Linq.Expressions;

using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql;

/// <summary>
/// Creates symbolic SQL expressions retained by the C# database schema model.
/// </summary>
public static class Sql
{
    /// <summary>
    /// Declares a filtered sum over a table row type.
    /// </summary>
    /// <typeparam name="TSource">The table row type.</typeparam>
    /// <param name="selector">Selects the value to sum.</param>
    /// <param name="predicate">Selects the rows included in the aggregate.</param>
    /// <returns>A symbolic aggregate for schema compilation.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="selector"/> or <paramref name="predicate"/> is null.
    /// </exception>
    public static ISqlAggregateExpression Sum<TSource>(
        Expression<Func<TSource, object?>> selector,
        Expression<Func<TSource, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(predicate);
        return new SqlAggregateExpression(typeof(TSource), selector, predicate);
    }
}
