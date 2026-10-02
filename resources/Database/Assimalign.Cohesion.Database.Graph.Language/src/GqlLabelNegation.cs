namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>
/// A label negation <c>!</c>: matches an element its operand does not match. ISO/IEC 39075 negates
/// one label primary, so the parser reads <c>!A&amp;B</c> as <c>(!A)&amp;B</c> and rejects
/// <c>!!A</c>; write <c>!(!A)</c>.
/// </summary>
/// <param name="Operand">The negated expression.</param>
public sealed record GqlLabelNegation(GqlLabelExpression Operand) : GqlLabelExpression;
