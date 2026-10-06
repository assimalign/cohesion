namespace Assimalign.Cohesion.Database.Documents.Catalog.Internal;

/// <summary>
/// One persisted catalog record of the metadata owner: exactly one of a collection, a document or
/// an index definition.
/// </summary>
/// <param name="Collection">The collection metadata, when the record is one.</param>
/// <param name="Document">The document metadata, when the record is one.</param>
/// <param name="Index">The index definition, when the record is one.</param>
internal readonly record struct CatalogRecord(DocumentCollectionMetadata? Collection, DocumentCatalogEntry? Document, DocumentIndexMetadata? Index = null);
