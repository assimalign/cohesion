using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>
/// Requests the target column's default as a whole INSERT VALUES item or UPDATE SET value.
/// </summary>
/// <remarks>This contextually typed node is not a scalar operand.</remarks>
public sealed class SqlDefaultExpression : SqlExpression
{
    /// <summary>Initializes a DEFAULT item.</summary>
    /// <param name="location">The keyword's source location.</param>
    internal SqlDefaultExpression(Location? location)
        : base(location)
    {
    }
}
