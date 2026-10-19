namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A parenthesized path, optionally repeated as one segment.</summary>
/// <param name="Pattern">The enclosed path.</param>
/// <param name="Quantifier">The inclusive repetition bounds, or null for one occurrence.</param>
public sealed record GqlParenthesizedPathPattern(GqlPathPattern Pattern, GqlQuantifier? Quantifier = null) : GqlPathElement;
