namespace Assimalign.Cohesion.Database.Graph.Storage;

/// <summary>A visible node-property index: its label, property key and text discriminator.</summary>
/// <param name="Label">The node label.</param><param name="PropertyKey">The property name.</param>
/// <param name="IsText">Whether the index contains Unicode trigrams.</param>
public readonly record struct StoredGraphIndex(string Label, string PropertyKey, bool IsText = false);
