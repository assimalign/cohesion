using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Graph.Internal;

namespace Assimalign.Cohesion.Database.Graph;

/// <summary>An immutable directed edge type with references to source and target node types.</summary>
/// <remarks>GQ-T1 persists this declaration. Edge property and endpoint enforcement is supplied by GQ-T2.</remarks>
public sealed class GraphEdgeType
{
    [System.Diagnostics.DebuggerBrowsable(System.Diagnostics.DebuggerBrowsableState.Never)]
    internal GraphEdgeTypeDeclaration Declaration { get; }

    private GraphEdgeType(GraphEdgeTypeDeclaration declaration, IReadOnlyList<GraphPropertyType> properties)
        => (Declaration, RelationshipType, SourceNodeType, TargetNodeType, Properties) = (declaration, declaration.RelationshipType, declaration.SourceNodeType, declaration.TargetNodeType, properties);

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
        var copied = GraphType.CopyProperties(properties);
        return new(GraphEdgeTypeDeclaration.Create(relationshipType, sourceNodeType, targetNodeType, copied.Select(item => item.Declaration)), copied);
    }

    internal static GraphEdgeType FromDeclaration(GraphEdgeTypeDeclaration declaration)
        => new(declaration, Array.AsReadOnly(declaration.Properties.Select(GraphPropertyType.FromDeclaration).ToArray()));
}
