using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Graph.Internal;

namespace Assimalign.Cohesion.Database.Graph;

/// <summary>An immutable node type selected by its identifying label.</summary>
public sealed class GraphNodeType
{
    [System.Diagnostics.DebuggerBrowsable(System.Diagnostics.DebuggerBrowsableState.Never)]
    internal GraphNodeTypeDeclaration Declaration { get; }

    private GraphNodeType(GraphNodeTypeDeclaration declaration, IReadOnlyList<GraphPropertyType> properties)
        => (Declaration, IdentifyingLabel, ImpliedLabels, Properties) = (declaration, declaration.IdentifyingLabel, declaration.ImpliedLabels, properties);

    /// <summary>Gets the ordinal label identifying members of this type.</summary>
    public string IdentifyingLabel { get; }
    /// <summary>Gets the additional labels every member must already carry.</summary>
    public IReadOnlyList<string> ImpliedLabels { get; }
    /// <summary>Gets the declared properties; additional properties remain permitted.</summary>
    public IReadOnlyList<GraphPropertyType> Properties { get; }

    /// <summary>Creates a node type with copied, ordinally ordered declarations.</summary>
    /// <param name="identifyingLabel">The label selecting members.</param>
    /// <param name="impliedLabels">Required additional labels, or null for none.</param>
    /// <param name="properties">Property declarations, or null for none.</param>
    /// <returns>The immutable node type.</returns>
    /// <exception cref="ArgumentException">A name is blank, a label is repeated or identifies this type, or a property is null or repeated.</exception>
    public static GraphNodeType Create(string identifyingLabel, IEnumerable<string>? impliedLabels = null,
        IEnumerable<GraphPropertyType>? properties = null)
    {
        var copied = GraphType.CopyProperties(properties);
        return new(GraphNodeTypeDeclaration.Create(identifyingLabel, impliedLabels, copied.Select(item => item.Declaration)), copied);
    }

    internal static GraphNodeType FromDeclaration(GraphNodeTypeDeclaration declaration)
        => new(declaration, Array.AsReadOnly(declaration.Properties.Select(GraphPropertyType.FromDeclaration).ToArray()));
}
