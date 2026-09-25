using System;

namespace Assimalign.Cohesion.Database.Blob.Catalog;

/// <summary>Identifies a container and the authority that owns its definition.</summary>
/// <param name="Id">The stable identity, distinct from any previously dropped container with the same name.</param>
/// <param name="Name">The case-sensitive container name.</param>
/// <param name="Owner">The authority that created the container.</param>
/// <param name="OwningSchema">The schema name for a schema-owned container; otherwise null.</param>
public readonly record struct BlobContainerMetadata(
    Guid Id,
    string Name,
    DatabaseObjectOwner Owner = DatabaseObjectOwner.Adhoc,
    string? OwningSchema = null);
