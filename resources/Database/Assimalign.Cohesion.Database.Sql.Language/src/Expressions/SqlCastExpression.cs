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
    /// <param name="targetTypeInfo">
    /// The resolved target type and constraints, or null for a target that is not a built-in type
    /// name, or that is invalid.
    /// </param>
    /// <param name="location">The source location.</param>
    internal SqlCastExpression(SqlExpression operand, string targetType, DatabaseTypeInfo? targetTypeInfo, Location? location)
        : base(location)
    {
        Operand = operand;
        TargetType = targetType;
        TargetTypeInfo = targetTypeInfo;
        Depth = 1 + operand.Depth;
    }

    /// <summary>
    /// Gets the expression being cast.
    /// </summary>
    public SqlExpression Operand { get; }

    /// <summary>
    /// Gets the target data type as written, with its type arguments (<c>VARCHAR(10)</c>,
    /// <c>email</c>).
    /// </summary>
    public string TargetType { get; }

    /// <summary>
    /// Gets the target's shared type identity and explicit length, precision, or scale constraints
    /// when the target is a built-in type name of the dialect.
    /// </summary>
    /// <remarks>
    /// Null in two cases. A target whose type arguments are invalid is accompanied by a parser error
    /// diagnostic. A target that is not a built-in type name has none: the parser does not know the
    /// engine's types, so the engine resolves <see cref="TargetType"/> against its type catalog,
    /// where an application registers domains, and reports a name that is not there.
    /// </remarks>
    public DatabaseTypeInfo? TargetTypeInfo { get; }
}
