namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>The unmatched INSERT action. Its target binding is MISSING.</summary>
/// <param name="Key">The ANSI insert key; null for lookup INSERT, which uses ON KEY.</param>
/// <param name="Value">The inserted document expression.</param>
/// <param name="Predicate">The optional action WHERE expression.</param>
public sealed record OqlMergeInsertAction(OqlExpression? Key, OqlExpression Value, OqlExpression? Predicate = null);
