using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A MATCH or OPTIONAL MATCH with its own graph pattern and filter.</summary>
/// <param name="Paths">The finite paths joined within this clause.</param>
/// <param name="Predicate">The optional filter over variables bound through this clause.</param>
/// <param name="Optional">Whether an unmatched input row keeps its bindings and nulls new variables.</param>
public sealed record GqlMatchClause(IReadOnlyList<GqlPathPattern> Paths, GqlExpression? Predicate, bool Optional);
