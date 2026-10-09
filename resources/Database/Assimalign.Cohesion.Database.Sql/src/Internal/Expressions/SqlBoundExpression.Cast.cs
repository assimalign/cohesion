using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// <c>CAST</c> over a bound operand. The target is the one the language parser resolved on the CAST
/// node, which the bound node refers to rather than copies; the conversion itself is
/// <see cref="SqlCastConverter"/>'s, unchanged.
/// </summary>
internal sealed class SqlBoundCast : SqlBoundExpression
{
    /// <summary>Initializes a bound CAST.</summary>
    /// <param name="operand">The bound operand.</param>
    /// <param name="cast">The CAST node, which carries the resolved target type.</param>
    internal SqlBoundCast(SqlBoundExpression operand, SqlCastExpression cast)
        : base(SqlBoundExpressionKind.Cast)
    {
        Operand = operand;
        Cast = cast;
    }

    /// <summary>Gets the bound operand.</summary>
    internal SqlBoundExpression Operand { get; }

    /// <summary>Gets the CAST node, which carries the resolved target type.</summary>
    internal SqlCastExpression Cast { get; }
}
