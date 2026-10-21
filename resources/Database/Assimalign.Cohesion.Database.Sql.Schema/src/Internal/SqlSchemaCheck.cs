namespace Assimalign.Cohesion.Database.Sql.Schema.Internal;

/// <summary>A CHECK constraint, its predicate SQL text as the author wrote it.</summary>
/// <param name="Name">The constraint name.</param>
/// <param name="Sql">The predicate's SQL text.</param>
internal sealed record SqlSchemaCheck(string Name, string Sql);
