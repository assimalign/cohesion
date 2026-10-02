namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A label disjunction <c>|</c>: matches an element either operand matches.</summary>
/// <param name="Left">The left operand.</param>
/// <param name="Right">The right operand.</param>
public sealed record GqlLabelDisjunction(GqlLabelExpression Left, GqlLabelExpression Right) : GqlLabelExpression;
