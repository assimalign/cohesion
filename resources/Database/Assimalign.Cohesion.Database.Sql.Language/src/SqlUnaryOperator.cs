namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>
/// Classifies the operator used in a unary expression.
/// </summary>
public enum SqlUnaryOperator
{
    /// <summary>Logical NOT.</summary>
    Not,

    /// <summary>Arithmetic negation (<c>-</c>).</summary>
    Negate,

    /// <summary>Bitwise NOT (<c>~</c>).</summary>
    BitwiseNot,
}
