using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A linear sequence between NEXT boundaries.</summary>
/// <param name="Stages">The operations in source order, excluding NEXT itself.</param>
public sealed record GqlQuerySegment(IReadOnlyList<GqlStage> Stages);
