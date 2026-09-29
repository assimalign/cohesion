using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A node pattern with an optional binding, labels, and literal property constraints.</summary>
/// <param name="Variable">The binding name, or null for an anonymous node.</param>
/// <param name="Labels">The required labels.</param>
/// <param name="Properties">The literal properties; null, Boolean, integer, floating point, or string.</param>
public sealed record GqlNodePattern(string? Variable, IReadOnlyList<string> Labels,
    IReadOnlyDictionary<string, object?> Properties);
