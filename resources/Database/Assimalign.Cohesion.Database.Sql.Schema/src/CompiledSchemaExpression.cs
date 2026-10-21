namespace Assimalign.Cohesion.Database.Sql.Schema;

/// <summary>Contains the portable canonical form of an analyzable expression.</summary>
/// <param name="CanonicalText">The normalized expression AST text.</param>
public sealed record CompiledSchemaExpression(string CanonicalText);
