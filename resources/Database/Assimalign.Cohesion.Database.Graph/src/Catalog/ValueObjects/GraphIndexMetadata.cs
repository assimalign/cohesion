using System;

namespace Assimalign.Cohesion.Database.Graph.Catalog;

/// <summary>A named nonunique secondary node-property index definition.</summary>
/// <param name="LabelId">The indexed label's stable identity.</param>
/// <param name="Name">The ordinal name, unique across all database indexes and constraints.</param>
/// <param name="PropertyKey">The indexed scalar property key.</param>
public readonly record struct GraphIndexMetadata(Guid LabelId, string Name, string PropertyKey);
