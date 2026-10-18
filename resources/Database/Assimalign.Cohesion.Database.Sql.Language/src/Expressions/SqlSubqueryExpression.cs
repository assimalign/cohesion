namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

/// <summary>
/// Represents a parenthesized subquery used in a scalar position, such as <c>(SELECT MAX(Id) FROM Users)</c>.
/// </summary>
public sealed class SqlSubqueryExpression : SqlExpression
{
    /// <summary>
    /// Initializes a new <see cref="SqlSubqueryExpression"/>.
    /// </summary>
    /// <param name="select">The query body forming the subquery.</param>
    /// <param name="location">The source location.</param>
    internal SqlSubqueryExpression(SqlQueryBodyExpression select, Location? location)
        : base(location)
    {
        Select = select;
        Depth = 1 + select.ExpressionDepth;
    }

    /// <summary>
    /// Gets the SELECT or set-operation body forming the subquery.
    /// </summary>
    public SqlQueryBodyExpression Select { get; }
}
