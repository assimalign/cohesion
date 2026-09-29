using System;

namespace Assimalign.Cohesion.Database.Blob.Catalog;

/// <summary>Describes a complete blob version and references the head of its immutable chunk chain.</summary>
/// <param name="ContainerId">The containing container's stable identity.</param>
/// <param name="Name">The case-sensitive blob name.</param>
/// <param name="Length">The complete content length in bytes.</param>
/// <param name="ContentType">The declared media type, or null.</param>
/// <param name="ETag">The opaque version tag.</param>
/// <param name="CreatedAt">The original creation time.</param>
/// <param name="ModifiedAt">The time this version was written.</param>
/// <param name="Checksum">The CRC-32 checksum of the complete content.</param>
/// <param name="HeadLocation">The encoded head chunk location; zero for empty content.</param>
public readonly record struct BlobCatalogEntry(
    Guid ContainerId,
    string Name,
    long Length,
    string? ContentType,
    ulong ETag,
    DateTimeOffset CreatedAt,
    DateTimeOffset ModifiedAt,
    uint Checksum,
    ulong HeadLocation);
