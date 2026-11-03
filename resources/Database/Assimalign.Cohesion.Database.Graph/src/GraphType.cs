using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Graph.Internal;

namespace Assimalign.Cohesion.Database.Graph;

/// <summary>An immutable, open, database-scoped graph type.</summary>
/// <remarks>Membership derives from labels. Unmentioned labels, relationship types and properties remain writable.</remarks>
public sealed class GraphType
{
    [System.Diagnostics.DebuggerBrowsable(System.Diagnostics.DebuggerBrowsableState.Never)]
    internal GraphTypeDeclaration Declaration { get; }

    private GraphType(GraphTypeDeclaration declaration, IReadOnlyList<GraphNodeType> nodeTypes, IReadOnlyList<GraphEdgeType> edgeTypes)
        => (Declaration, NodeTypes, EdgeTypes) = (declaration, nodeTypes, edgeTypes);

    /// <summary>Gets node types in ordinal identifying-label order.</summary>
    public IReadOnlyList<GraphNodeType> NodeTypes { get; }
    /// <summary>Gets edge types in ordinal relationship-type order.</summary>
    public IReadOnlyList<GraphEdgeType> EdgeTypes { get; }

    /// <summary>Creates an open graph type, copying the element collections.</summary>
    /// <param name="nodeTypes">Node declarations, or null for none.</param>
    /// <param name="edgeTypes">Edge declarations, or null for none.</param>
    /// <returns>The immutable type. An empty type is valid.</returns>
    /// <exception cref="ArgumentException">An element is null or its identifying name is repeated within its kind.</exception>
    /// <remarks>Edge references are checked by schema mutation against the resulting complete type, allowing ADD/ALTER fragments.</remarks>
    public static GraphType Create(IEnumerable<GraphNodeType>? nodeTypes = null, IEnumerable<GraphEdgeType>? edgeTypes = null)
    {
        var nodes = GraphTypeDeclaration.CopyItems(nodeTypes, item => item.IdentifyingLabel);
        var edges = GraphTypeDeclaration.CopyItems(edgeTypes, item => item.RelationshipType);
        return new(GraphTypeDeclaration.Create(nodes.Select(item => item.Declaration), edges.Select(item => item.Declaration)), nodes, edges);
    }

    internal static GraphType FromDeclaration(GraphTypeDeclaration declaration)
        => new(declaration, Array.AsReadOnly(declaration.NodeTypes.Select(GraphNodeType.FromDeclaration).ToArray()),
            Array.AsReadOnly(declaration.EdgeTypes.Select(GraphEdgeType.FromDeclaration).ToArray()));

    internal static IReadOnlyList<GraphPropertyType> CopyProperties(IEnumerable<GraphPropertyType>? properties)
        => GraphTypeDeclaration.CopyItems(properties, item => item.Name, nameof(properties));
}
