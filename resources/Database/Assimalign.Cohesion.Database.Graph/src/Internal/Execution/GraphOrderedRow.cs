using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>One materialized projection and its pre-evaluated ordering keys.</summary>
internal sealed record GraphOrderedRow(object?[] Values, object?[] Keys, long Ordinal)
{
    internal IReadOnlyDictionary<string, object?>? Scope { get; init; }
    internal long RetainedBytes { get; set; }
}
