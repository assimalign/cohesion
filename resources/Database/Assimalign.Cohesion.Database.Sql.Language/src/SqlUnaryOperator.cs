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

    /// <summary>
    /// ISO unary plus (<c>+</c>): the operand's value with its type unchanged. A <c>+</c>
    /// directly before a numeric literal is part of the literal and builds no unary node.
    /// </summary>
    Plus,
}
