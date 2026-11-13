using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A node element declaration in a graph-type command.</summary>
/// <param name="Label">The identifying label.</param>
/// <param name="ImpliedLabels">Additional required labels.</param>
/// <param name="Properties">Scalar property declarations.</param>
public sealed record GqlGraphNodeType(string Label, IReadOnlyList<string> ImpliedLabels, IReadOnlyList<GqlGraphPropertyType> Properties);
