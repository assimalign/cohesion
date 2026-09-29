using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Graph.Storage;

/// <summary>A durable property-graph node.</summary>
/// <param name="Id">Stable nonzero identity.</param><param name="Labels">Ordinal labels.</param><param name="Properties">Scalar properties.</param>
public readonly record struct StoredGraphNode(ulong Id, IReadOnlyList<string> Labels, IReadOnlyDictionary<string, object?> Properties);

/// <summary>A durable directed relationship and its endpoints.</summary>
/// <param name="Id">Stable nonzero identity.</param><param name="SourceId">Source node identity.</param><param name="TargetId">Target node identity.</param><param name="Type">Relationship type.</param><param name="Properties">Scalar properties.</param>
public readonly record struct StoredGraphRelationship(ulong Id, ulong SourceId, ulong TargetId, string Type, IReadOnlyDictionary<string, object?> Properties);
