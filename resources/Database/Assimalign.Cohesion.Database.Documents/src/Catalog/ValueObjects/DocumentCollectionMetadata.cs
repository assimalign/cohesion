using System;

namespace Assimalign.Cohesion.Database.Documents.Catalog;

/// <summary>Identifies a collection and the authority that owns its definition.</summary>
/// <param name="Id">The stable identity, distinct from any previously dropped collection with the same name.</param>
/// <param name="Name">The case-sensitive collection name.</param>
/// <param name="Owner">The authority that created the collection.</param>
/// <param name="OwningSchema">The schema name for a schema-owned collection; otherwise null.</param>
public readonly record struct DocumentCollectionMetadata(
    Guid Id,
    string Name,
    DatabaseObjectOwner Owner = DatabaseObjectOwner.Adhoc,
    string? OwningSchema = null);

