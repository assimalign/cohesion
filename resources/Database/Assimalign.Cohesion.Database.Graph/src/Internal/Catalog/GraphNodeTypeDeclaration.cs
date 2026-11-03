using System;
using System.Collections.Generic;
using System.Linq;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>An immutable node type selected by its identifying label.</summary>
internal sealed class GraphNodeTypeDeclaration
{
    private GraphNodeTypeDeclaration(string identifyingLabel, IReadOnlyList<string> impliedLabels, IReadOnlyList<GraphPropertyTypeDeclaration> properties)
        => (IdentifyingLabel, ImpliedLabels, Properties) = (identifyingLabel, impliedLabels, properties);

    /// <summary>Gets the ordinal label identifying members of this type.</summary>
    public string IdentifyingLabel { get; }
    /// <summary>Gets the additional labels every member must already carry.</summary>
    public IReadOnlyList<string> ImpliedLabels { get; }
    /// <summary>Gets the declared properties; additional properties remain permitted.</summary>
    public IReadOnlyList<GraphPropertyTypeDeclaration> Properties { get; }

    /// <summary>Creates a node type with copied, ordinally ordered declarations.</summary>
    /// <param name="identifyingLabel">The label selecting members.</param>
    /// <param name="impliedLabels">Required additional labels, or null for none.</param>
    /// <param name="properties">Property declarations, or null for none.</param>
    /// <returns>The immutable node type.</returns>
    /// <exception cref="ArgumentException">A name is blank, a label is repeated or identifies this type, or a property is null or repeated.</exception>
    public static GraphNodeTypeDeclaration Create(string identifyingLabel, IEnumerable<string>? impliedLabels = null,
        IEnumerable<GraphPropertyTypeDeclaration>? properties = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifyingLabel);
        string[] labels = (impliedLabels ?? []).ToArray();
        foreach (string label in labels) { ArgumentException.ThrowIfNullOrWhiteSpace(label); }
        if (labels.Contains(identifyingLabel, StringComparer.Ordinal) || labels.Distinct(StringComparer.Ordinal).Count() != labels.Length)
        { throw new ArgumentException(ErrorMessages.GraphTypeDuplicate, nameof(impliedLabels)); }
        return new(identifyingLabel, Array.AsReadOnly(labels.Order(StringComparer.Ordinal).ToArray()), GraphTypeDeclaration.CopyProperties(properties));
    }
}
