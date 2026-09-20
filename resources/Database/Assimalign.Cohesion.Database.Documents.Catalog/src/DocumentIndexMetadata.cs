using System;

namespace Assimalign.Cohesion.Database.Documents.Catalog;

/// <summary>Describes one nonunique secondary index over a scalar document path.</summary>
/// <param name="CollectionId">The collection identity.</param>
/// <param name="Name">The ordinal index name, unique in the collection.</param>
/// <param name="Path">The case-sensitive field path with optional array subscripts.</param>
/// <param name="ObjectId">The stable physical index generation identity.</param>
public readonly record struct DocumentIndexMetadata(Guid CollectionId, string Name, string Path, ulong ObjectId);
