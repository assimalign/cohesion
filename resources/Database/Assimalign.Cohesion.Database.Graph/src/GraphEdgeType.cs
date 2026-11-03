using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Graph;

/// <summary>An immutable directed edge type with references to source and target node types.</summary>
/// <remarks>GQ-T1 persists this declaration. Edge property and endpoint enforcement is supplied by GQ-T2.</remarks>
public sealed class GraphEdgeType
{
    private GraphEdgeType(string relationshipType, string sourceNodeType, string targetNodeType, IReadOnlyList<GraphPropertyType> properties)
        => (RelationshipType, SourceNodeType, TargetNodeType, Properties) = (relationshipType, sourceNodeType, targetNodeType, properties);

    /// <summary>Gets the ordinal relationship type identifying members.</summary>
    public string RelationshipType { get; }
    /// <summary>Gets the identifying label of the referenced source node type.</summary>
    public string SourceNodeType { get; }
    /// <summary>Gets the identifying label of the referenced target node type.</summary>
    public string TargetNodeType { get; }
    /// <summary>Gets the declared edge properties.</summary>
    public IReadOnlyList<GraphPropertyType> Properties { get; }

    /// <summary>Creates an edge declaration; references are resolved against the complete graph type.</summary>
    /// <param name="relationshipType">The relationship type.</param>
    /// <param name="sourceNodeType">The source node type's identifying label.</param>
    /// <param name="targetNodeType">The target node type's identifying label.</param>
    /// <param name="properties">Property declarations, or null for none.</param>
    /// <returns>The immutable edge type.</returns>
    /// <exception cref="ArgumentException">A name is blank or a property is null or repeated.</exception>
    public static GraphEdgeType Create(string relationshipType, string sourceNodeType, string targetNodeType,
        IEnumerable<GraphPropertyType>? properties = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relationshipType);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceNodeType);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetNodeType);
        return new(relationshipType, sourceNodeType, targetNodeType, GraphType.CopyProperties(properties));
    }
}
