namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A scalar property declaration in a graph type.</summary>
/// <param name="Name">The property key.</param>
/// <param name="Type">The scalar type.</param>
/// <param name="Required">Whether NOT NULL is required.</param>
public sealed record GqlGraphPropertyType(string Name, GqlSchemaPropertyType Type, bool Required);
