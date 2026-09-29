namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Types;

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
    /// <param name="targetTypeInfo">The resolved target type and constraints, or null for an invalid target.</param>
    /// <param name="location">The source location.</param>
    internal SqlCastExpression(SqlExpression operand, string targetType, DatabaseTypeInfo? targetTypeInfo, Location? location)
        : base(location)
    {
        Operand = operand;
        TargetType = targetType;
        TargetTypeInfo = targetTypeInfo;
    }

    /// <summary>
    /// Gets the expression being cast.
    /// </summary>
    public SqlExpression Operand { get; }

    /// <summary>
    /// Gets the target data type name.
    /// </summary>
    public string TargetType { get; }

    /// <summary>
    /// Gets the target's shared type identity and explicit length, precision, or scale constraints.
    /// Invalid targets have no resolved type and are accompanied by a parser error diagnostic.
    /// </summary>
    public DatabaseTypeInfo? TargetTypeInfo { get; }
}
