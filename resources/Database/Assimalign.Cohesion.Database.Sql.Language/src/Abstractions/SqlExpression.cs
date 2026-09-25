namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

/// <summary>
/// Base class for all SQL scalar expressions that can appear in WHERE, HAVING,
/// SET, SELECT lists, and other value positions.
/// </summary>
public abstract class SqlExpression : QueryExpression
{
    /// <summary>
    /// Initializes a new <see cref="SqlExpression"/>.
    /// </summary>
    /// <param name="location">The source location of the expression.</param>
    protected internal SqlExpression(Location? location)
        : base(null, location ?? Location.Create(1, 1, 0, 0))
    {
    }
}
