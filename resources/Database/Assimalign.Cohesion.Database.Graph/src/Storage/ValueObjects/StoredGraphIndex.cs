namespace Assimalign.Cohesion.Database.Graph.Storage;

/// <summary>A visible exact node-property index: the label and property key it covers.</summary>
/// <param name="Label">The node label.</param><param name="PropertyKey">The property name.</param>
public readonly record struct StoredGraphIndex(string Label, string PropertyKey);
