using System;
using System.Collections.Generic;
using System.Linq;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>An immutable, open, database-scoped graph type.</summary>
/// <remarks>Membership derives from labels. Unmentioned labels, relationship types and properties remain writable.</remarks>
internal sealed class GraphTypeDeclaration
{
    private GraphTypeDeclaration(IReadOnlyList<GraphNodeTypeDeclaration> nodeTypes, IReadOnlyList<GraphEdgeTypeDeclaration> edgeTypes)
        => (NodeTypes, EdgeTypes) = (nodeTypes, edgeTypes);

    /// <summary>Gets node types in ordinal identifying-label order.</summary>
    public IReadOnlyList<GraphNodeTypeDeclaration> NodeTypes { get; }
    /// <summary>Gets edge types in ordinal relationship-type order.</summary>
    public IReadOnlyList<GraphEdgeTypeDeclaration> EdgeTypes { get; }

    /// <summary>Creates an open graph type, copying the element collections.</summary>
    /// <param name="nodeTypes">Node declarations, or null for none.</param>
    /// <param name="edgeTypes">Edge declarations, or null for none.</param>
    /// <returns>The immutable type. An empty type is valid.</returns>
    /// <exception cref="ArgumentException">An element is null or its identifying name is repeated within its kind.</exception>
    /// <remarks>Edge references are checked by schema mutation against the resulting complete type, allowing ADD/ALTER fragments.</remarks>
    public static GraphTypeDeclaration Create(IEnumerable<GraphNodeTypeDeclaration>? nodeTypes = null, IEnumerable<GraphEdgeTypeDeclaration>? edgeTypes = null)
    {
        return new(CopyItems(nodeTypes, item => item.IdentifyingLabel), CopyItems(edgeTypes, item => item.RelationshipType));
    }

    internal static IReadOnlyList<GraphPropertyTypeDeclaration> CopyProperties(IEnumerable<GraphPropertyTypeDeclaration>? properties)
        => CopyItems(properties, item => item.Name, nameof(properties));

    internal static IReadOnlyList<T> CopyItems<T>(IEnumerable<T>? values, Func<T, string> name, string? paramName = null) where T : class
    {
        T[] items = (values ?? []).ToArray();
        if (items.Any(item => item is null) || items.Select(name).Distinct(StringComparer.Ordinal).Count() != items.Length)
        { throw new ArgumentException(ErrorMessages.GraphTypeDuplicate, paramName); }
        return Array.AsReadOnly(items.OrderBy(name, StringComparer.Ordinal).ToArray());
    }
}
