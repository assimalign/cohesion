namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>The operator that joins the operands of a <see cref="GqlLogicalExpression"/>.</summary>
public enum GqlLogicalOperator
{
    /// <summary>
    /// Logical <c>AND</c> under ISO/IEC 39075 three-valued logic: <see langword="false"/> when any
    /// operand is false, otherwise unknown (<see langword="null"/>) when any operand is unknown,
    /// otherwise <see langword="true"/>.
    /// </summary>
    And,
    /// <summary>True if any operand is true; otherwise unknown if any is unknown, otherwise false.</summary>
    Or,
    /// <summary>Unknown if any operand is unknown; otherwise the parity of the true operands.</summary>
    Xor,
}
