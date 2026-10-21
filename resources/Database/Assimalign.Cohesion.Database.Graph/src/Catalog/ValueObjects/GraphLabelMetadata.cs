using System;

namespace Assimalign.Cohesion.Database.Graph.Catalog;

/// <summary>A node-label definition and the authority that owns it.</summary>
/// <param name="Id">The stable definition identity.</param>
/// <param name="Name">The ordinal, case-sensitive label name.</param>
/// <param name="Owner">The authority owning the definition.</param>
/// <param name="OwningSchema">The schema name when the definition is schema-owned.</param>
public readonly record struct GraphLabelMetadata(Guid Id, string Name,
    DatabaseObjectOwner Owner = DatabaseObjectOwner.Adhoc, string? OwningSchema = null);
