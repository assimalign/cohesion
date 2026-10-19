namespace Assimalign.Cohesion.Database.Blob.Catalog.Internal;

/// <summary>
/// One persisted catalog record of the metadata owner: exactly one of a container or a blob.
/// </summary>
/// <param name="Container">The container metadata, when the record is one.</param>
/// <param name="Blob">The blob metadata, when the record is one.</param>
internal readonly record struct CatalogRecord(BlobContainerMetadata? Container, BlobCatalogEntry? Blob);
