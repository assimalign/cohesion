namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

/// <summary>
/// Represents the <c>*</c> wildcard in a SELECT list or <c>COUNT(*)</c>.
/// </summary>
public sealed class SqlStarExpression : SqlExpression
{
    /// <summary>
    /// Initializes a new <see cref="SqlStarExpression"/>.
    /// </summary>
    /// <param name="location">The source location.</param>
    internal SqlStarExpression(Location? location)
        : base(location)
    {
    }
}
