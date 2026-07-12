namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

/// <summary>
/// Represents a <c>CAST</c> expression such as <c>CAST(x AS INT)</c>.
/// </summary>
public sealed class SqlCastExpression : SqlExpression
{
    /// <summary>
    /// Initializes a new <see cref="SqlCastExpression"/>.
    /// </summary>
    /// <param name="operand">The expression being cast.</param>
    /// <param name="targetType">The target data type name.</param>
    /// <param name="location">The source location.</param>
    internal SqlCastExpression(SqlExpression operand, string targetType, Location? location)
        : base(location)
    {
        Operand = operand;
        TargetType = targetType;
    }

    /// <summary>
    /// Gets the expression being cast.
    /// </summary>
    public SqlExpression Operand { get; }

    /// <summary>
    /// Gets the target data type name.
    /// </summary>
    public string TargetType { get; }
}
