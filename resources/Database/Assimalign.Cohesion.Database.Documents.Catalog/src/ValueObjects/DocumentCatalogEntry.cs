using System;

namespace Assimalign.Cohesion.Database.Documents.Catalog;

/// <summary>Describes a document version and its immutable UTF-8 JSON chunk chain.</summary>
/// <param name="CollectionId">The containing collection identity.</param>
/// <param name="Id">The ordinal document identity.</param>
/// <param name="Version">The monotonically increasing version.</param>
/// <param name="HeadLocation">The packed first chunk location.</param>
/// <param name="Length">The complete content length.</param>
/// <param name="Checksum">The IEEE CRC-32 of the original UTF-8 JSON bytes.</param>
public readonly record struct DocumentCatalogEntry(Guid CollectionId, string Id, ulong Version,
    ulong HeadLocation, long Length, uint Checksum)
{
    internal string Name => Id;
}
