using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

/// <summary>
/// Represents an <c>IN</c> predicate such as <c>x IN (1, 2, 3)</c> or <c>x IN (SELECT ...)</c>.
/// </summary>
public sealed class SqlInExpression : SqlExpression
{
    /// <summary>
    /// Initializes a new <see cref="SqlInExpression"/>.
    /// </summary>
    /// <param name="operand">The expression being tested.</param>
    /// <param name="values">The list of values, if using a value list.</param>
    /// <param name="subquery">The subquery, if using a subquery.</param>
    /// <param name="isNegated">Whether the predicate is negated (<c>NOT IN</c>).</param>
    /// <param name="location">The source location.</param>
    internal SqlInExpression(SqlExpression operand, IReadOnlyList<SqlExpression>? values, SqlSelectExpression? subquery, bool isNegated, Location? location)
        : base(location)
    {
        Operand = operand;
        Values = values;
        Subquery = subquery;
        IsNegated = isNegated;
    }

    /// <summary>
    /// Gets the expression being tested.
    /// </summary>
    public SqlExpression Operand { get; }

    /// <summary>
    /// Gets the list of values, if this is a value-list form.
    /// </summary>
    public IReadOnlyList<SqlExpression>? Values { get; }

    /// <summary>
    /// Gets the subquery, if this is a subquery form.
    /// </summary>
    public SqlSelectExpression? Subquery { get; }

    /// <summary>
    /// Gets whether the predicate is negated (<c>NOT IN</c>).
    /// </summary>
    public bool IsNegated { get; }
}
