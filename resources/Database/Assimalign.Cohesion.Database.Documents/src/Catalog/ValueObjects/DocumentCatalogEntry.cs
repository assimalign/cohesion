using System;

using Assimalign.Cohesion.Database.Documents.Protocol;

namespace Assimalign.Cohesion.Database.Documents.Catalog;

/// <summary>Describes a document version and its immutable UTF-8 JSON chunk chain.</summary>
/// <param name="CollectionId">The containing collection identity.</param>
/// <param name="Id">The ordinal document identity.</param>
/// <param name="Version">The monotonically increasing version.</param>
/// <param name="HeadLocation">The packed first chunk location.</param>
/// <param name="Length">The complete content length.</param>
/// <param name="Checksum">The IEEE CRC-32 of the original UTF-8 JSON bytes.</param>
[global::System.Diagnostics.DebuggerDisplay("DocumentCatalogEntry Id={Id}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Documents.Catalog.DocumentCatalogEntry.DebugView))]
public readonly record struct DocumentCatalogEntry(Guid CollectionId, string Id, ulong Version,
    ulong HeadLocation, long Length, uint Checksum)
{
    internal string Name => Id;

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Documents.Catalog.DocumentCatalogEntry _target;

        public DebugView(global::Assimalign.Cohesion.Database.Documents.Catalog.DocumentCatalogEntry value)
        {
            _target = value;
        }

        public global::System.Guid CollectionId => _target.CollectionId;
        public string Id => _target.Id;
        public ulong Version => _target.Version;
        public ulong HeadLocation => _target.HeadLocation;
        public long Length => _target.Length;
        public uint Checksum => _target.Checksum;
    }
}
