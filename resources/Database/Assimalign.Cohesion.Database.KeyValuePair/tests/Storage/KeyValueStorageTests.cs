using System;
using System.IO;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.KeyValuePair.Storage.Tests;

/// <summary>
/// The key-value storage binding: entry records on the shared page/WAL substrate,
/// keyed by key-space owner id, surviving a reopen over both file assets.
/// </summary>
public class KeyValueStorageTests
{
    private const ulong keySpaceId = 1;

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair.Storage] - Create: Should report the key-value storage model")]
    public void Create_NewStorage_ShouldReportKeyValueModel()
    {
        // Arrange / Act
        using var storage = KeyValueStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "kv");

        // Assert
        storage.Model.ShouldBe(StorageModel.KeyValue);
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair.Storage] - InsertEntry: Should round-trip entry bytes through the owner chain")]
    public void InsertEntry_IntoKeySpaceChain_ShouldRoundTrip()
    {
        // Arrange
        using var storage = KeyValueStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "kv");
        byte[] entry = [1, 2, 3, 4, 5];

        // Act
        (PageId pageId, int slotIndex) location;
        using (var transaction = storage.BeginTransaction())
        {
            location = storage.InsertEntry(transaction, keySpaceId, entry);
            transaction.Commit();
        }

        // Assert
        storage.ReadEntry(location.pageId, location.slotIndex).ToArray().ShouldBe(entry);
        storage.GetOwnerPages(keySpaceId).ShouldContain(location.pageId);
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair.Storage] - Open: Should recover committed entries across a reopen of both file assets")]
    public void Open_AfterCommittedInsert_ShouldRecoverEntries()
    {
        // Arrange: both the data and journal streams must survive the "reopen" —
        // the WAL is no-force, so the journal may carry state the data file lacks.
        var data = new MemoryStream();
        var journal = new MemoryStream();
        var backup = new MemoryStream();
        byte[] entry = [42, 43, 44];

        (PageId pageId, int slotIndex) location;
        using (var storage = KeyValueStorage.Create(new NonClosingStream(data), new NonClosingStream(journal), new NonClosingStream(backup), "kv"))
        {
            using var transaction = storage.BeginTransaction();
            location = storage.InsertEntry(transaction, keySpaceId, entry);
            transaction.Commit();
        }

        // Act: reopen from the surviving streams.
        data.Position = 0;
        journal.Position = 0;
        backup.Position = 0;

        using var reopened = KeyValueStorage.Open(new NonClosingStream(data), new NonClosingStream(journal), new NonClosingStream(backup));

        // Assert
        reopened.ReadEntry(location.pageId, location.slotIndex).ToArray().ShouldBe(entry);
    }

    /// <summary>
    /// Keeps the underlying buffer alive when the storage disposes its streams, so
    /// the test can hand the same bytes to a reopened instance.
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
