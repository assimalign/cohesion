namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>The standalone schema DDL targets.</summary>
public enum GqlSchemaObjectKind
{
    /// <summary>A single node-property index.</summary>
    Index,
    /// <summary>A named type or existence constraint.</summary>
    Constraint,
    /// <summary>A node label definition.</summary>
    Label,
    /// <summary>A relationship type definition.</summary>
    RelationshipType,
}
