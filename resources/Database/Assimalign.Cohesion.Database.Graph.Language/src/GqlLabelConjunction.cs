namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>
/// A label conjunction <c>&amp;</c>: matches an element both operands match. The Cohesion
/// repeated-colon convenience <c>:A:B</c> produces the same tree as <c>:A&amp;B</c>.
/// </summary>
/// <param name="Left">The left operand.</param>
/// <param name="Right">The right operand.</param>
public sealed record GqlLabelConjunction(GqlLabelExpression Left, GqlLabelExpression Right) : GqlLabelExpression;
