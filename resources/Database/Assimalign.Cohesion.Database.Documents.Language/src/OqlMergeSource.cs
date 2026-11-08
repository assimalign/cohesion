namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>A MERGE source; a subquery requires an alias, while a collection or path can supply an implicit name.</summary>
/// <param name="Collection">The keyspace name; empty for an expression source.</param>
/// <param name="Alias">The explicit alias; null uses the collection or final path member.</param>
/// <param name="Expression">The source expression, including a subquery; null selects the collection.</param>
public sealed record OqlMergeSource(string Collection, string? Alias, OqlExpression? Expression = null);
