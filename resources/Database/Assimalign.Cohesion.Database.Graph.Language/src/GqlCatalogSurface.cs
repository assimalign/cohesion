namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A database-scoped catalog subject accepted by the Cohesion SHOW extension.</summary>
public enum GqlCatalogSurface
{
    /// <summary>Node label definitions.</summary>
    Labels,
    /// <summary>Relationship type definitions.</summary>
    RelationshipTypes,
    /// <summary>Declared and discovered property keys of labels and relationship types.</summary>
    PropertyKeys,
    /// <summary>Named node-property index definitions.</summary>
    Indexes,
    /// <summary>The authority governing each definition and its child metadata.</summary>
    ObjectOwnership,
}
