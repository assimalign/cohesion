using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A relationship pattern between consecutive node patterns.</summary>
/// <param name="Variable">The optional relationship binding.</param>
/// <param name="Type">The optional required relationship type.</param>
/// <param name="Direction">The direction relative to the source pattern's node order.</param>
/// <param name="Properties">The literal property constraints.</param>
public sealed record GqlRelationshipPattern(string? Variable, string? Type,
    GqlPatternDirection Direction, IReadOnlyDictionary<string, object?> Properties);
