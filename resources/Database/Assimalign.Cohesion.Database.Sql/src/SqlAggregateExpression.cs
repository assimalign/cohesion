using System;
using System.Linq.Expressions;

namespace Assimalign.Cohesion.Database.Sql;

/// <summary>
/// A symbolic aggregate in a C# database function declaration, created by
/// <see cref="Sql.Sum{TSource}"/>.
/// </summary>
/// <remarks>
/// <b>Shape (concrete-types plan, phase 4, row 81).</b> A public sealed class with get-only
/// members and an internal constructor; it replaced the <c>ISqlAggregateExpression</c> interface
/// over an internal positional record. It is not a record: a public positional record would expose
/// a public constructor and a <c>with</c> clone around <see cref="Sql.Sum{TSource}"/>'s argument
/// checks.
/// </remarks>
public sealed class SqlAggregateExpression
{
    internal SqlAggregateExpression(Type sourceType, LambdaExpression selector, LambdaExpression predicate)
    {
        SourceType = sourceType;
        Selector = selector;
        Predicate = predicate;
    }

    /// <summary>Gets the source table row type.</summary>
    public Type SourceType { get; }

    /// <summary>Gets the aggregate value selector.</summary>
    public LambdaExpression Selector { get; }

    /// <summary>Gets the aggregate row predicate.</summary>
    public LambdaExpression Predicate { get; }
}
