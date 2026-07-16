using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.KeyValuePair.Storage;

namespace Assimalign.Cohesion.Database.KeyValuePair.Catalog.Tests;

/// <summary>
/// The key-value catalog: index registrations and the entry-space format version
/// persist through the dedicated catalog file set and reload across a reopen.
/// </summary>
public class KeyValueCatalogTests
{
    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair.Catalog] - Open: Should report defaults on an empty catalog")]
    public void Open_EmptyCatalog_ShouldReportDefaults()
    {
        // Arrange
        using var storage = KeyValueStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "kv.catalog");

        // Act
        var catalog = KeyValueCatalog.Open(storage);

        // Assert
        catalog.EntrySpaceFormatVersion.ShouldBe(1);
        catalog.GetIndexRegistrations().ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair.Catalog] - SaveIndexRegistrations: Should persist and reload registrations across a reopen")]
    public async Task SaveIndexRegistrations_AcrossReopen_ShouldReload()
    {
        // Arrange: both file assets survive the reopen (WAL is no-force).
        var data = new MemoryStream();
        var journal = new MemoryStream();
        var registrations = new List<BTreeIndexRegistration>
        {
            new(1, new IndexDefinition("key", IndexKind.BTree, IsUnique: true), RootPageId: 7),
        };

        using (var storage = KeyValueStorage.Create(new NonClosingStream(data), new NonClosingStream(journal), new MemoryStream(), "kv.catalog"))
        {
            var catalog = KeyValueCatalog.Open(storage);

            // Act
            await catalog.SaveIndexRegistrationsAsync(registrations);
            await catalog.SetEntrySpaceFormatVersionAsync(1);
        }

        data.Position = 0;
        journal.Position = 0;

        using (var reopened = KeyValueStorage.Open(new NonClosingStream(data), new NonClosingStream(journal), new MemoryStream()))
        {
            var catalog = KeyValueCatalog.Open(reopened);

            // Assert
            catalog.EntrySpaceFormatVersion.ShouldBe(1);
            var loaded = catalog.GetIndexRegistrations().ShouldHaveSingleItem();
            loaded.ObjectId.ShouldBe(1UL);
            loaded.Definition.Name.ShouldBe("key");
            loaded.Definition.IsUnique.ShouldBeTrue();
            loaded.RootPageId.ShouldBe(7);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair.Catalog] - SaveIndexRegistrations: Should replace the stored set on re-export")]
    public async Task SaveIndexRegistrations_OnReExport_ShouldReplaceStoredSet()
    {
        // Arrange
        using var storage = KeyValueStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "kv.catalog");
        var catalog = KeyValueCatalog.Open(storage);

        await catalog.SaveIndexRegistrationsAsync(new List<BTreeIndexRegistration>
        {
            new(1, new IndexDefinition("key", IndexKind.BTree, IsUnique: true), RootPageId: 3),
        });

        // Act: a root split drifted the root page id; the engine re-exports.
        await catalog.SaveIndexRegistrationsAsync(new List<BTreeIndexRegistration>
        {
            new(1, new IndexDefinition("key", IndexKind.BTree, IsUnique: true), RootPageId: 11),
        });

        // Assert
        catalog.GetIndexRegistrations().ShouldHaveSingleItem().RootPageId.ShouldBe(11);
    }

    /// <summary>
    /// Keeps the underlying buffer alive when the storage disposes its streams.
    /// </summary>
    private sealed class NonClosingStream : Stream
    {
        private readonly MemoryStream _inner;

        internal NonClosingStream(MemoryStream inner) => _inner = inner;

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        protected override void Dispose(bool disposing) { /* leave the inner stream open */ }
    }
}
