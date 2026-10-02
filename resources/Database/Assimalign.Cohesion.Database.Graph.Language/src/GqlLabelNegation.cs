namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>
/// A label negation <c>!</c>: matches an element its operand does not match. ISO/IEC 39075 negates
/// one label primary, so the parser reads <c>!A&amp;B</c> as <c>(!A)&amp;B</c> and rejects
/// <c>!!A</c>; write <c>!(!A)</c>.
/// </summary>
/// <param name="Operand">The negated expression.</param>
public sealed record GqlLabelNegation(GqlLabelExpression Operand) : GqlLabelExpression
{
    /// <summary>
    /// Whether <paramref name="other"/> is the same tree. A chain of negations can be as deep as
    /// the parser's stack allowed, so the comparison walks it with an explicit stack.
    /// </summary>
    /// <param name="other">The negation to compare.</param>
    /// <returns><see langword="true"/> when the trees are equal.</returns>
    public bool Equals(GqlLabelNegation? other) => other is not null && StructurallyEqual(this, other);

    /// <inheritdoc />
    public override int GetHashCode() => StructuralHash(this);
}
