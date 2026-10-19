namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>
/// Classifies the operator that joins the operands of a <see cref="SqlLogicalExpression"/>.
/// </summary>
public enum SqlLogicalOperator
{
    /// <summary>
    /// Logical <c>AND</c>: <see langword="false"/> when any operand is <see langword="false"/>,
    /// otherwise unknown (<c>NULL</c>) when any operand is unknown, otherwise
    /// <see langword="true"/>.
    /// </summary>
    And,

    /// <summary>
    /// Logical <c>OR</c>: <see langword="true"/> when any operand is <see langword="true"/>,
    /// otherwise unknown (<c>NULL</c>) when any operand is unknown, otherwise
    /// <see langword="false"/>.
    /// </summary>
    Or,
}
