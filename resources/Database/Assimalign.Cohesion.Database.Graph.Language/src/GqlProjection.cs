namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A projected graph element or one of its scalar properties.</summary>
/// <param name="Variable">The bound node or relationship variable.</param>
/// <param name="Property">The property name, or null to project the entire element.</param>
/// <param name="Alias">The optional result column name.</param>
public sealed record GqlProjection(string Variable, string? Property = null, string? Alias = null);
