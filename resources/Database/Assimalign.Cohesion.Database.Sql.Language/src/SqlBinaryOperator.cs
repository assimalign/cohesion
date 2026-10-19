namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>
/// Classifies the operator used in a binary expression.
/// </summary>
public enum SqlBinaryOperator
{
    /// <summary>Addition (<c>+</c>).</summary>
    Add,

    /// <summary>Subtraction (<c>-</c>).</summary>
    Subtract,

    /// <summary>Multiplication (<c>*</c>).</summary>
    Multiply,

    /// <summary>Division (<c>/</c>).</summary>
    Divide,

    /// <summary>Modulo (<c>%</c>).</summary>
    Modulo,

    /// <summary>Equality (<c>=</c>).</summary>
    Equal,

    /// <summary>Inequality (<c>&lt;&gt;</c> or <c>!=</c>).</summary>
    NotEqual,

    /// <summary>Less than (<c>&lt;</c>).</summary>
    LessThan,

    /// <summary>Greater than (<c>&gt;</c>).</summary>
    GreaterThan,

    /// <summary>Less than or equal (<c>&lt;=</c>).</summary>
    LessOrEqual,

    /// <summary>Greater than or equal (<c>&gt;=</c>).</summary>
    GreaterOrEqual,

    /// <summary>
    /// Logical AND. The parser no longer builds a binary node with this operator: every
    /// <c>AND</c> chain is a <see cref="SqlLogicalExpression"/> (#1151).
    /// </summary>
    And,

    /// <summary>
    /// Logical OR. The parser no longer builds a binary node with this operator: every
    /// <c>OR</c> chain is a <see cref="SqlLogicalExpression"/> (#1151).
    /// </summary>
    Or,

    /// <summary>String concatenation (<c>||</c>).</summary>
    Concat,
}
