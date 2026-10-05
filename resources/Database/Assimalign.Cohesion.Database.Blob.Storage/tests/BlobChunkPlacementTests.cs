using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Transactions;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Blob.Storage.Tests;

/// <summary>
/// Where content chunks land. Every logical transaction used to own its chunk pages (owner
/// <c>writer | 1 &lt;&lt; 63</c>), so each small upload took a fresh 8 KiB page that no later
/// transaction wrote to: four times the space of a 2 KiB blob. Chunks now share one content
/// owner, so successive uploads fill the same page, as PostgreSQL's relation target block
/// (<c>hio.c</c>, <c>RelationGetBufferForTuple</c>) and RavenDB's table section (<c>Table.cs</c>,
/// <c>Insert</c>) do.
/// </summary>
public sealed class BlobChunkPlacementTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Blob.Storage] - OpenWrite: small uploads of separate transactions share data pages")]
    public async Task OpenWrite_SmallUploadsInSeparateTransactions_ShouldShareDataPages()
    {
        // Arrange
        const int uploads = 300;
        using var storage = BlobStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "test");
        await using var coordinator = new TransactionCoordinator(storage, storage.WriteAheadJournal, storage.Records);
        long pagesBefore = storage.PageManager.PageCount;
        var written = new List<(BlobContentReference Content, byte[] Bytes)>(uploads);

        // Act: each upload is its own logical transaction, as an automatic upload is.
        for (int id = 0; id < uploads; id++)
        {
            byte[] bytes = Pattern(2048, id);
            written.Add((await WriteAsync(storage, coordinator, bytes), bytes));
        }

        // Assert: a 2,076-byte chunk record fits three to a page, so 300 uploads need 100 pages;
        // one page per transaction took 300.
        long pages = storage.PageManager.PageCount - pagesBefore;
        pages.ShouldBeLessThanOrEqualTo(uploads / 3 + 1, $"{pages} data pages for {uploads} one-chunk uploads");
        foreach (var (content, bytes) in written)
        {
            using var read = storage.OpenRead(content);
            var actual = new MemoryStream();
            await read.CopyToAsync(actual);
            actual.ToArray().ShouldBe(bytes);
        }
    }

    private static async Task<BlobContentReference> WriteAsync(BlobStorage storage, TransactionCoordinator coordinator, byte[] content)
    {
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        BlobContentReference reference = default;
        await using (var upload = storage.OpenWrite(coordinator, writer, async value =>
        {
            reference = value;
            await coordinator.CommitAsync(writer);
        }, _ => coordinator.RollbackAsync(writer)))
        {
            await upload.WriteAsync(content.AsMemory());
        }

        return reference;
    }

    private static byte[] Pattern(int count, int seed)
    {
        var content = new byte[count];
        for (int index = 0; index < content.Length; index++)
        {
            content[index] = (byte)((index * 31 + seed) % 251);
        }

        return content;
    }
}
