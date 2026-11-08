namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>The matched DELETE action.</summary>
/// <param name="Predicate">The optional action WHERE expression.</param>
public sealed record OqlMergeDeleteAction(OqlExpression? Predicate = null);
