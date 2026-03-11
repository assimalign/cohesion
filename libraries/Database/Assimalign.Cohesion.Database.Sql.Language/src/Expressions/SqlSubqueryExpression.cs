namespace Assimalign.Cohesion.Database.Language.Sql;

using Assimalign.Cohesion.Database.Language;

/// <summary>
/// Represents a parenthesized subquery used in a scalar position, such as <c>(SELECT MAX(Id) FROM Users)</c>.
/// </summary>
public sealed class SqlSubqueryExpression : SqlExpression
{
    /// <summary>
    /// Initializes a new <see cref="SqlSubqueryExpression"/>.
    /// </summary>
    /// <param name="select">The SELECT expression forming the subquery.</param>
    /// <param name="location">The source location.</param>
    internal SqlSubqueryExpression(SqlSelectExpression select, Location? location)
        : base(location)
    {
        Select = select;
    }

    /// <summary>
    /// Gets the SELECT expression forming the subquery.
    /// </summary>
    public SqlSelectExpression Select { get; }
}
