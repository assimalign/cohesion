using System;

namespace Assimalign.Cohesion.Database.Blob;

/// <summary>
/// The catalog metadata of a stored blob.
/// </summary>
/// <param name="Name">The blob name within its container.</param>
/// <param name="Length">The content length in bytes.</param>
/// <param name="ContentType">The declared media type of the content, or null when undeclared.</param>
/// <param name="ETag">An opaque version tag that changes on every write, for optimistic concurrency.</param>
/// <param name="CreatedAt">When the blob was first written.</param>
/// <param name="ModifiedAt">When the blob content was last replaced.</param>
/// <param name="Checksum">The CRC-32 checksum of the content, for integrity verification.</param>
public readonly record struct BlobProperties(
    string Name,
    long Length,
    string? ContentType,
    ulong ETag,
    DateTimeOffset CreatedAt,
    DateTimeOffset ModifiedAt,
    uint Checksum);
