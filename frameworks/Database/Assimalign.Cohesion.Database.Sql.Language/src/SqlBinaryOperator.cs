namespace Assimalign.Cohesion.Database.Language.Sql;

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

    /// <summary>Logical AND.</summary>
    And,

    /// <summary>Logical OR.</summary>
    Or,

    /// <summary>String concatenation (<c>||</c>).</summary>
    Concat,
}
