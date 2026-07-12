namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

/// <summary>
/// Represents an <c>EXISTS</c> predicate such as <c>EXISTS (SELECT ...)</c>.
/// </summary>
public sealed class SqlExistsExpression : SqlExpression
{
    /// <summary>
    /// Initializes a new <see cref="SqlExistsExpression"/>.
    /// </summary>
    /// <param name="subquery">The subquery being tested.</param>
    /// <param name="isNegated">Whether the predicate is negated (<c>NOT EXISTS</c>).</param>
    /// <param name="location">The source location.</param>
    internal SqlExistsExpression(SqlSelectExpression subquery, bool isNegated, Location? location)
        : base(location)
    {
        Subquery = subquery;
        IsNegated = isNegated;
    }

    /// <summary>
    /// Gets the subquery being tested for existence.
    /// </summary>
    public SqlSelectExpression Subquery { get; }

    /// <summary>
    /// Gets whether the predicate is negated (<c>NOT EXISTS</c>).
    /// </summary>
    public bool IsNegated { get; }
}
