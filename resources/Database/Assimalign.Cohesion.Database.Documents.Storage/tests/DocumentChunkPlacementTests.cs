using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Transactions;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Storage.Tests;

/// <summary>
/// Where content chunks land. Every logical transaction used to own its chunk pages (owner
/// <c>writer | 1 &lt;&lt; 63</c>), so each one-chunk document took a fresh 8 KiB page that no
/// later transaction wrote to: about 39 times the space of a 180-byte document, and the engine's
/// in-memory data file passed 2 GiB in one six-second pace test on a fast runner. Chunks now share
/// one content owner, so successive transactions fill the same page, as PostgreSQL's relation
/// target block (<c>hio.c</c>, <c>RelationGetBufferForTuple</c>) and RavenDB's table section
/// (<c>Table.cs</c>, <c>Insert</c>) do.
/// </summary>
public sealed class DocumentChunkPlacementTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Documents.Storage] - WriteContent: small documents of separate transactions share data pages")]
    public async Task WriteContent_SmallDocumentsInSeparateTransactions_ShouldShareDataPages()
    {
        // Arrange
        const int documents = 400;
        using var storage = Create();
        await using var coordinator = Coordinate(storage);
        long pagesBefore = storage.PageManager.PageCount;
        var written = new List<(DocumentContentReference Content, byte[] Bytes)>(documents);

        // Act: each document is its own logical transaction, as an auto-commit put is.
        for (int id = 0; id < documents; id++)
        {
            var context = await coordinator.BeginAsync(IsolationLevel.Snapshot);
            byte[] bytes = Doc(id);
            var content = await storage.WriteContentAsync(coordinator, context, bytes);
            await coordinator.CommitAsync(context);
            written.Add((content, bytes));
        }

        // Assert: a chunk record of about 210 bytes fits 38 to a page, so 400 documents need
        // 11 pages; one page per transaction took 400.
        long pages = storage.PageManager.PageCount - pagesBefore;
        pages.ShouldBeLessThanOrEqualTo(documents / 20, $"{pages} data pages for {documents} one-chunk documents");
        foreach (var (content, bytes) in written)
        {
            storage.ReadContent(content).ToArray().ShouldBe(bytes);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Documents.Storage] - WriteContent: transactions sharing a page keep the committed chunk through a rollback and a crash")]
    public async Task WriteContent_TransactionsShareAPage_ShouldKeepCommittedAndRemoveRolledBackAndAbandonedChunks()
    {
        // Arrange: three open transactions write one chunk each, interleaved with nothing between.
        var data = new MemoryStream();
        var journal = new MemoryStream();
        using var storage = DocumentStorage.Create(data, journal, new MemoryStream(), "shared");
        await using var coordinator = Coordinate(storage);
        var committed = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var rolledBack = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var abandoned = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        byte[] committedBytes = Doc(1);
        var committedContent = await storage.WriteContentAsync(coordinator, committed, committedBytes);
        var rolledBackContent = await storage.WriteContentAsync(coordinator, rolledBack, Doc(2));
        var abandonedContent = await storage.WriteContentAsync(coordinator, abandoned, Doc(3));

        // Act: one commits, one rolls back, and the process stops with the third still open.
        await coordinator.CommitAsync(committed);
        await coordinator.RollbackAsync(rolledBack);
        int afterRollback = Count(storage);
        byte[] readAfterRollback = storage.ReadContent(committedContent).ToArray();
        storage.WriteAheadJournal.Flush(forceDurable: false);
        using var recovered = DocumentStorage.Open(Clone(data), Clone(journal), new MemoryStream(), false);
        await using var recovery = Coordinate(recovered);
        recovery.AnalyzeAndScrub();
        recovery.CompleteRecovery();

        // Assert: the three chunks shared one page; the rollback removed only its own chunk, and
        // recovery removed only the abandoned one.
        var page = DocumentStorage.UnpackLocation(committedContent.Head).PageId;
        DocumentStorage.UnpackLocation(rolledBackContent.Head).PageId.ShouldBe(page);
        DocumentStorage.UnpackLocation(abandonedContent.Head).PageId.ShouldBe(page);
        afterRollback.ShouldBe(2);
        readAfterRollback.ShouldBe(committedBytes);
        Count(recovered).ShouldBe(1);
        recovered.ReadContent(committedContent).ToArray().ShouldBe(committedBytes);
        await coordinator.RollbackAsync(abandoned);
    }

    // A document of the engines' pace-test shape: about 180 bytes.
    private static byte[] Doc(int id)
        => Encoding.UTF8.GetBytes($"{{\"id\":\"k{id}\",\"payload\":\"{new string('x', 150)}\"}}");

    private static DocumentStorage Create() => DocumentStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "test");

    private static TransactionCoordinator Coordinate(DocumentStorage storage) => new(storage, storage.WriteAheadJournal, storage.Records);

    private static int Count(DocumentStorage storage)
    {
        using var iterator = storage.GetUnitIterator();
        int count = 0;
        while (iterator.MoveNext())
        {
            count++;
        }

        return count;
    }

    private static MemoryStream Clone(MemoryStream source)
    {
        var clone = new MemoryStream();
        source.WriteTo(clone);
        clone.Position = 0;
        return clone;
    }
}
