using System;
using System.IO;
using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage.Internal;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Storage.Tests;

/// <summary>
/// The checkpoint anchor (#1226 integration review). A checkpoint truncates the journal and then
/// appends a record listing the logical transactions whose begin records the truncation destroyed.
/// When that append fails, or the process stops before the record is flushed, nothing in the
/// journal names those transactions any more; the list the checkpoint wrote into the file header
/// before the truncation still does.
/// </summary>
public sealed class StorageCheckpointAnchorTests
{
    [Fact(DisplayName = "Cohesion Test [Storage] - Checkpoint anchor: the active transactions a checkpoint records survive a crash")]
    public void Checkpoint_WithActiveTransactions_ShouldRecordThemInTheFileHeader()
    {
        // Arrange
        using var storage = AnchorStorage.Create();

        // Act
        storage.Checkpoint([5, 9]);
        var images = storage.CaptureImages();
        using var reopened = AnchorStorage.Open(images.Data, images.Journal);

        // Assert
        storage.CheckpointActiveTransactions.ShouldBe([5L, 9L]);
        reopened.CheckpointActiveTransactions.ShouldBe([5L, 9L]);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Checkpoint anchor: a checkpoint record lost after the truncation leaves the anchor")]
    public void Checkpoint_RecordAppendFailsAfterTheTruncation_ShouldKeepTheAnchor()
    {
        // Arrange: a journal with records, and a checkpoint whose own record cannot be appended.
        using var storage = AnchorStorage.Create();
        storage.Insert("kept");
        storage.JournalStream.FailWrites = 1;

        // Act
        Should.Throw<IOException>(() => storage.Checkpoint([7]));
        var images = storage.CaptureImages();
        using var reopened = AnchorStorage.Open(images.Data, images.Journal);

        // Assert: the truncation emptied the journal, and only the anchor still names the
        // transaction; the data the checkpoint flushed survives.
        storage.JournalStream.FailWrites.ShouldBe(0);
        images.Journal.ShouldBeEmpty();
        reopened.Log.ReadAll().ShouldBeEmpty();
        reopened.CheckpointActiveTransactions.ShouldBe([7L]);
        reopened.CountRecords().ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Checkpoint anchor: an anchor larger than a header slot chains anchor pages instead of refusing the checkpoint")]
    [InlineData(StorageHeaderPage.InlineAnchorCapacity)]
    [InlineData(StorageHeaderPage.InlineAnchorCapacity + 1)]
    [InlineData(981)]
    [InlineData(StorageHeaderPage.InlineAnchorCapacity + (3 * StorageHeaderPage.AnchorPageCapacity))]
    [InlineData(25_000)]
    public void Checkpoint_MoreSequencesThanASlotHolds_ShouldChainAnchorPagesAndRoundTrip(int count)
    {
        // Arrange: the anchor used to refuse anything above 980 sequences as busy (#1242).
        using var storage = AnchorStorage.Create();
        storage.Insert("kept");
        long[] anchor = [.. Enumerable.Range(1, count).Select(i => (long)i * 7)];
        int expectedPages = (Math.Max(0, count - StorageHeaderPage.InlineAnchorCapacity) + StorageHeaderPage.AnchorPageCapacity - 1)
            / StorageHeaderPage.AnchorPageCapacity;

        // Act: twice, so both header slots carry a chain.
        storage.Checkpoint(anchor);
        storage.Checkpoint(anchor);
        var images = storage.CaptureImages();
        using var reopened = AnchorStorage.Open(images.Data, images.Journal);

        // Assert
        storage.CheckpointActiveTransactions.ShouldBe(anchor);
        reopened.CheckpointActiveTransactions.ShouldBe(anchor);
        CountAnchorPages(images.Data).ShouldBe(2 * expectedPages);
        reopened.CountRecords().ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Checkpoint anchor: a shrinking anchor returns its chain's pages to the allocator")]
    public void Checkpoint_AnchorShrinks_ShouldFreeTheChainPages()
    {
        // Arrange: both slots chain three pages.
        using var storage = AnchorStorage.Create();
        long[] large = [.. Enumerable.Range(1, StorageHeaderPage.InlineAnchorCapacity + (2 * StorageHeaderPage.AnchorPageCapacity) + 1).Select(i => (long)i)];
        storage.Checkpoint(large);
        storage.Checkpoint(large);
        long pagesWithChains = storage.PageManager.PageCount;
        long freeWithChains = storage.PageManager.FreePageCount;

        // Act: two small checkpoints release both chains; inserts then reuse the pages.
        storage.Checkpoint([4, 2]);
        storage.Checkpoint([4]);
        long freeAfterShrink = storage.PageManager.FreePageCount;
        var images = storage.CaptureImages();
        using var reopened = AnchorStorage.Open(images.Data, images.Journal);

        // Assert: every chain page is free again, on disk too, and the file did not grow.
        freeAfterShrink.ShouldBe(freeWithChains + 6);
        storage.PageManager.PageCount.ShouldBe(pagesWithChains);
        CountAnchorPages(images.Data).ShouldBe(0);
        reopened.CheckpointActiveTransactions.ShouldBe([4L]);
        reopened.PageManager.FreePageCount.ShouldBe(freeAfterShrink);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Checkpoint anchor: recovery never replays a stale image onto a page the live anchor chain reuses")]
    public void Checkpoint_ChainReusesPagesFreedSinceTheLastCheckpoint_ShouldSurviveRecoveryBeforeTheTruncation()
    {
        // Arrange: pages freed by a committed transaction keep their images (Free pages) in
        // the journal until the next truncation; the next header write reuses them for its chain.
        var point = new CrashPoint();
        var storage = TornStorage.Create(point); // abandoned after its simulated power loss
        var pages = storage.FillPages(4);
        storage.Checkpoint();
        storage.FreeAll(pages);
        long[] anchor = [.. Enumerable.Range(1, StorageHeaderPage.InlineAnchorCapacity + StorageHeaderPage.AnchorPageCapacity + 5).Select(i => (long)i)];

        // Act: the checkpoint loses power at the journal truncation — after its header slot
        // is durable, before the journal loses the Free images of the reused pages.
        point.CrashWhen = (stream, operation, _, _) => stream == "journal" && operation == "SetLength";
        SimulatedPowerLossException.Expect(() => storage.Checkpoint(anchor));
        var images = storage.CaptureDurable();
        using var reopened = TornStorage.Open(images);
        long chainPage = reopened.AnchorChainPages[0];
        var afterRecovery = reopened.CaptureDurable();
        using var reopenedAgain = TornStorage.Open(afterRecovery);

        // Assert: the freed page's Free image was still in the journal recovery replayed, yet the
        // chain page holding the new anchor survived it — on disk and in the allocator.
        pages.ShouldContain(chainPage);
        new StreamJournal(new MemoryStream(images.Journal)).ReadAll()
            .Count(record => record.Type == JournalRecordType.AfterPageImage && (long)record.PageId == chainPage)
            .ShouldBeGreaterThan(0);
        reopened.CheckpointActiveTransactions.ShouldBe(anchor);
        reopened.FreeSpaceMap.IsAllocated((PageId)chainPage).ShouldBeTrue();
        ((PageType)afterRecovery.Data[(chainPage * Units.Page.Size) + Units.Page.TypeFieldOffset]).ShouldBe(PageType.CheckpointAnchor);
        reopenedAgain.CheckpointActiveTransactions.ShouldBe(anchor);
    }

    /// <summary>
    /// A bracket committed with <c>awaitDurability: false</c> (a statement bracket, an undo batch,
    /// a dropped table's release) returns its pages to the allocator as soon as its commit record
    /// is appended, while that record may still sit in a journal tail that is not durable. An
    /// anchor page is written outside the journal: reusing such a page and making it durable
    /// first would overwrite the page's committed content while the bracket that freed it can
    /// still vanish in a crash, and the content would be lost with nothing to restore it.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Storage] - Checkpoint anchor: a chain page reuses a page only once the commit that freed it is durable")]
    public void Checkpoint_ChainReusesAPageWhoseFreeIsNotDurable_ShouldMakeTheFreeDurableFirst()
    {
        // Arrange: one durable row alone on its page, then a bracket that frees the page and
        // commits without waiting for durability; the journal is flush-gated.
        var point = new CrashPoint();
        var storage = TornStorage.Create(point, journalWriteThrough: false); // abandoned after its simulated power loss
        var pages = storage.FillPages(1, owner: 9);
        storage.Checkpoint();
        long freer = storage.FreeOwner(9, awaitDurability: false);
        long[] anchor = [.. Enumerable.Range(1, StorageHeaderPage.InlineAnchorCapacity + 1).Select(i => (long)i)];

        // Act: a checkpoint whose anchor needs a chain page loses power at the truncation.
        point.CrashWhen = (stream, operation, _, _) => stream == "journal" && operation == "SetLength";
        SimulatedPowerLossException.Expect(() => storage.Checkpoint(anchor));
        var images = storage.CaptureDurable();
        using var reopened = TornStorage.Open(images);
        bool freeIsDurable = new StreamJournal(new MemoryStream(images.Journal)).ReadAll()
            .Any(record => record.Type == JournalRecordType.CommitTransaction && record.TransactionSequence == freer);

        // Assert: the row is gone only if the bracket that freed it survived, and it did — the
        // free was durable before the chain reused the page.
        reopened.AnchorChainPages.ShouldContain(pages[0]);
        reopened.CountRecords(owner: 9).ShouldBe(freeIsDurable ? 0 : 1, "a committed row was lost to an unjournaled page write");
        freeIsDurable.ShouldBeTrue();
        reopened.CheckpointActiveTransactions.ShouldBe(anchor);
    }

    /// <summary>
    /// The same rule for a header write that is not a checkpoint (<c>FlushChanges</c>), which
    /// carries the last checkpoint's anchor into the other slot: power lost before its slot
    /// write leaves the previous generation, and the free must still be in the durable journal.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Storage] - Checkpoint anchor: a non-checkpoint header write also reuses a freed page only once its free is durable")]
    public void FlushHeader_ChainReusesAPageWhoseFreeIsNotDurable_ShouldMakeTheFreeDurableFirst()
    {
        // Arrange: a durable row alone on its page, whose journal images the checkpoint then
        // truncates; slot 1 carries a chained anchor, and slot 0, which the next header write
        // targets, has no chain yet, so that write allocates a page — the one the bracket frees.
        var point = new CrashPoint();
        var storage = TornStorage.Create(point, journalWriteThrough: false); // abandoned after its simulated power loss
        var pages = storage.FillPages(1, owner: 9);
        long[] anchor = [.. Enumerable.Range(1, StorageHeaderPage.InlineAnchorCapacity + 1).Select(i => (long)i)];
        storage.Checkpoint(anchor);
        long freer = storage.FreeOwner(9, awaitDurability: false);

        // Act: power is lost at the header slot write, after the chain page reached the media.
        point.CrashWhen = (stream, operation, offset, _) => stream == "data" && operation == "Write" && offset == StorageHeaderPage.Slot0Offset;
        SimulatedPowerLossException.Expect(() => storage.FlushHeader());
        var images = storage.CaptureDurable();
        using var reopened = TornStorage.Open(images);
        bool freeIsDurable = new StreamJournal(new MemoryStream(images.Journal)).ReadAll()
            .Any(record => record.Type == JournalRecordType.CommitTransaction && record.TransactionSequence == freer);
        var chainPage = images.Data.AsSpan((int)(pages[0] * Units.Page.Size), Units.Page.Size);

        // Assert: the freed page was overwritten as an anchor page; the row is gone only if the
        // bracket that freed it survived, and it did.
        ((PageType)chainPage[Units.Page.TypeFieldOffset]).ShouldBe(PageType.CheckpointAnchor);
        reopened.CountRecords(owner: 9).ShouldBe(freeIsDurable ? 0 : 1, "a committed row was lost to an unjournaled page write");
        freeIsDurable.ShouldBeTrue();
        reopened.CheckpointActiveTransactions.ShouldBe(anchor);
    }

    private static int CountAnchorPages(byte[] data)
    {
        int count = 0;
        for (int offset = Units.Page.Size; offset + Units.Page.Size <= data.Length; offset += Units.Page.Size)
        {
            if ((PageType)data[offset + Units.Page.TypeFieldOffset] == PageType.CheckpointAnchor)
            {
                count++;
            }
        }

        return count;
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Checkpoint anchor: every checkpoint replaces it, and a close that does not truncate keeps it")]
    public void Checkpoint_Later_ShouldReplaceTheAnchorWhichANonTruncatingCloseKeeps()
    {
        // Arrange
        using var storage = AnchorStorage.Create();
        storage.Checkpoint([11, 12]);
        storage.Checkpoint([12]);
        var replaced = storage.CheckpointActiveTransactions;

        // Act: a storage bracket is still active at the close, so the close flushes without a
        // checkpoint, and the anchor stays as the last checkpoint wrote it.
        var open = storage.BeginTransaction();
        storage.Dispose();
        var images = storage.CaptureImages();
        using var reopened = AnchorStorage.Open(images.Data, images.Journal);
        var anchorAfterTheClose = reopened.CheckpointActiveTransactions;
        reopened.Checkpoint();

        // Assert
        open.IsActive.ShouldBeTrue();
        replaced.ShouldBe([12L]);
        anchorAfterTheClose.ShouldBe([12L]);
        reopened.CheckpointActiveTransactions.ShouldBeEmpty();
    }

    /// <summary>A storage over byte buffers that survive its disposal, with a journal that can fail writes.</summary>
    private sealed class AnchorStorage : Storage
    {
        private readonly MemoryStream _data;
        private readonly FaultingMemoryStream _journal;

        private AnchorStorage(MemoryStream data, FaultingMemoryStream journal, bool reopen)
            : base(
                new StorageStream(new SimulatedDurableFileHandle(data)),
                new StorageStream(new SimulatedDurableFileHandle(journal)),
                new StorageStream(new MemoryStream()))
        {
            _data = data;
            _journal = journal;
            if (reopen)
            {
                OpenExisting(checkpointOnOpen: false);
            }
            else
            {
                InitializeNew((Name)"anchor-harness");
            }
        }

        public override StorageModel Model => StorageModel.Custom;

        public FaultingMemoryStream JournalStream => _journal;

        public IStorageJournal Log => WriteAheadLog;

        public static AnchorStorage Create() => new(new MemoryStream(), new FaultingMemoryStream(), reopen: false);

        public static AnchorStorage Open(byte[] data, byte[] journal) => new(Copy(data), CopyJournal(journal), reopen: true);

        /// <summary>Gets the bytes the storage has written so far, as a crash would leave them.</summary>
        public (byte[] Data, byte[] Journal) CaptureImages() => (_data.ToArray(), _journal.ToArray());

        public void Insert(string text)
        {
            using var transaction = BeginTransaction();
            InsertRecord(transaction, System.Text.Encoding.UTF8.GetBytes(text));
            transaction.Commit();
        }

        public int CountRecords()
        {
            int count = 0;
            using var iterator = GetUnitIterator();
            while (iterator.MoveNext())
            {
                count++;
            }

            return count;
        }

        private static MemoryStream Copy(byte[] bytes)
        {
            var stream = new MemoryStream();
            stream.Write(bytes);
            stream.Position = 0;
            return stream;
        }

        private static FaultingMemoryStream CopyJournal(byte[] bytes)
        {
            var stream = new FaultingMemoryStream();
            stream.Write(bytes);
            stream.Position = 0;
            return stream;
        }
    }
}
