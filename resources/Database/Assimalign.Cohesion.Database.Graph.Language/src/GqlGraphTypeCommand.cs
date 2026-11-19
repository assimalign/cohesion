using System;
using System.Collections.Generic;
using System.Linq;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A standalone alteration of the database's open graph type.</summary>
public sealed record GqlGraphTypeCommand
{
    /// <summary>Gets the operation: SET, ADD, ALTER or DROP.</summary>
    public GqlGraphTypeOperation Operation { get; init; }
    /// <summary>Gets the node declarations, or identifying labels for DROP.</summary>
    public IReadOnlyList<GqlGraphNodeType> Nodes { get; init; } = [];
    /// <summary>Gets the edge declarations, or relationship types for DROP.</summary>
    public IReadOnlyList<GqlGraphEdgeType> Edges { get; init; } = [];

    /// <summary>Renders canonical DDL, ordering all declarations and quoting every identifier.</summary>
    /// <returns>A reparsable ALTER CURRENT GRAPH TYPE statement.</returns>
    public override string ToString()
        => "ALTER CURRENT GRAPH TYPE " + Operation.ToString().ToUpperInvariant() + " {" + string.Join(", ",
            Nodes.OrderBy(node => node.Label, StringComparer.Ordinal).Select(node => "(:" + Quote(node.Label) + " =>" +
                (Operation == GqlGraphTypeOperation.Drop ? "" :
                    (node.ImpliedLabels.Count == 0 ? "" : " :" + string.Join(" & ", node.ImpliedLabels.Order(StringComparer.Ordinal).Select(Quote))) + Properties(node.Properties)) + ")")
            .Concat(Edges.OrderBy(edge => edge.Type, StringComparer.Ordinal).Select(edge =>
                Endpoint(edge.Source) + "-[:" + Quote(edge.Type) + " =>" +
                (Operation == GqlGraphTypeOperation.Drop ? "" : Properties(edge.Properties)) + "]->" + Endpoint(edge.Target)))) + "}";

    private static string Endpoint(string label) => label.Length == 0 ? "()" : "(:" + Quote(label) + ")";

    private static string Properties(IReadOnlyList<GqlGraphPropertyType> properties) => properties.Count == 0 ? "" : " {" +
        string.Join(", ", properties.OrderBy(property => property.Name, StringComparer.Ordinal).Select(property => Quote(property.Name) + " :: " +
            property.Type.ToString().ToUpperInvariant() + (property.Required ? " NOT NULL" : ""))) + "}";
    private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}

/// <summary>The operations over the current graph type.</summary>
public enum GqlGraphTypeOperation
{
    /// <summary>Replaces the complete type.</summary>
    Set,
    /// <summary>Adds new element types.</summary>
    Add,
    /// <summary>Replaces existing element declarations.</summary>
    Alter,
    /// <summary>Drops selected elements.</summary>
    Drop,
}
