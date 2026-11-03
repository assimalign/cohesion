using System;
using System.Collections.Generic;
using System.Linq;

namespace Assimalign.Cohesion.Database.Graph;

/// <summary>An immutable, open, database-scoped graph type.</summary>
/// <remarks>Membership derives from labels. Unmentioned labels, relationship types and properties remain writable.</remarks>
public sealed class GraphType
{
    private GraphType(IReadOnlyList<GraphNodeType> nodeTypes, IReadOnlyList<GraphEdgeType> edgeTypes)
        => (NodeTypes, EdgeTypes) = (nodeTypes, edgeTypes);

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
        GraphNodeType[] nodes = (nodeTypes ?? []).ToArray();
        GraphEdgeType[] edges = (edgeTypes ?? []).ToArray();
        if (nodes.Any(item => item is null) || edges.Any(item => item is null) ||
            nodes.Select(item => item.IdentifyingLabel).Distinct(StringComparer.Ordinal).Count() != nodes.Length ||
            edges.Select(item => item.RelationshipType).Distinct(StringComparer.Ordinal).Count() != edges.Length)
        { throw new ArgumentException(ErrorMessages.GraphTypeDuplicate); }
        return new(Array.AsReadOnly(nodes.OrderBy(item => item.IdentifyingLabel, StringComparer.Ordinal).ToArray()),
            Array.AsReadOnly(edges.OrderBy(item => item.RelationshipType, StringComparer.Ordinal).ToArray()));
    }

    internal static IReadOnlyList<GraphPropertyType> CopyProperties(IEnumerable<GraphPropertyType>? properties)
    {
        GraphPropertyType[] items = (properties ?? []).ToArray();
        if (items.Any(item => item is null) || items.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != items.Length)
        { throw new ArgumentException(ErrorMessages.GraphTypeDuplicate, nameof(properties)); }
        return Array.AsReadOnly(items.OrderBy(item => item.Name, StringComparer.Ordinal).ToArray());
    }
}
