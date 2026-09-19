using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A finite chain of nodes joined by individual relationship patterns.</summary>
/// <param name="Nodes">The ordered node patterns; one more than the relationship count.</param>
/// <param name="Relationships">The relationships joining each consecutive pair of nodes.</param>
public sealed record GqlPathPattern(IReadOnlyList<GqlNodePattern> Nodes,
    IReadOnlyList<GqlRelationshipPattern> Relationships);
