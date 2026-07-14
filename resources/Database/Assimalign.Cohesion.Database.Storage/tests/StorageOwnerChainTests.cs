using System;
using System.Linq;
using System.Text;
using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Storage.Tests;

/// <summary>
/// Per-object record chains (#911): data pages carry an owner tag, inserts land on
/// the owner's current write page, owner-scoped iteration touches only the owner's
/// pages, and a chain release returns its pages to the allocator transactionally.
/// </summary>
public sealed class StorageOwnerChainTests
{
    /// <summary>
    /// A concrete storage exposing owner-scoped record operations for the tests.
    /// </summary>
    private sealed class ChainStorage : Storage
    {
        private ChainStorage(StorageStream data, StorageStream journal, int poolCapacity)
            : base(data, journal, new StorageStream(new System.IO.MemoryStream()), poolCapacity)
        {
        }

        public override StorageModel Model => StorageModel.Custom;

        public static ChainStorage Create(CrashSimulationStream data, CrashSimulationStream journal, int poolCapacity = 16)
        {
            var storage = new ChainStorage(new StorageStream(data), new StorageStream(journal), poolCapacity);
            storage.InitializeNew((Name)"owner-chains");
            return storage;
        }

        public static ChainStorage Open(byte[] data, byte[] journal, int poolCapacity = 16)
        {
            var storage = new ChainStorage(
                new StorageStream(new CrashSimulationStream(data, writeThrough: true)),
                new StorageStream(new CrashSimulationStream(journal)),
                poolCapacity);
            storage.OpenExisting();
            return storage;
        }

        public (PageId PageId, int SlotIndex) Insert(IStorageTransaction transaction, ulong ownerId, byte[] data)
            => InsertRecord(transaction, ownerId, data);

        public string[] ScanText(ulong ownerId)
        {
            var results = new System.Collections.Generic.List<string>();
            using var iterator = GetUnitIterator(ownerId);
            while (iterator.MoveNext())
            {
                results.Add(Encoding.UTF8.GetString(iterator.Current.Data.Span));
            }
            return results.ToArray();
        }
    }

    private static byte[] Text(string value) => Encoding.UTF8.GetBytes(value);

    /// <summary>
    /// Fills the owner's chain with enough records to span several pages.
    /// </summary>
    private static void FillOwner(ChainStorage storage, ulong ownerId, string prefix, int count, int recordSize = 512)
    {
        using var transaction = storage.BeginTransaction();

        for (int i = 0; i < count; i++)
        {
            byte[] record = new byte[recordSize];
            Encoding.UTF8.GetBytes($"{prefix}-{i}").CopyTo(record, 0);
            storage.Insert(transaction, ownerId, record);
        }

        transaction.Commit();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Owner chains: inserts segregate into disjoint per-owner pages")]
    public void InsertRecord_PerOwner_ShouldSegregatePages()
    {
        // Arrange
        var storage = ChainStorage.Create(new CrashSimulationStream(), new CrashSimulationStream());

        // Act: interleave two owners' inserts in one transaction.
        using (var transaction = storage.BeginTransaction())
        {
            for (int i = 0; i < 40; i++)
            {
                storage.Insert(transaction, 7, new byte[300]);
                storage.Insert(transaction, 9, new byte[300]);
            }

            transaction.Commit();
        }

        // Assert: the owners' page sets are non-empty and disjoint.
        var pagesOf7 = storage.GetOwnerPages(7).Select(page => (long)page).ToHashSet();
        var pagesOf9 = storage.GetOwnerPages(9).Select(page => (long)page).ToHashSet();

        pagesOf7.ShouldNotBeEmpty();
        pagesOf9.ShouldNotBeEmpty();
        pagesOf7.Overlaps(pagesOf9).ShouldBeFalse();

        storage.ScanText(7).Length.ShouldBe(40);
        storage.ScanText(9).Length.ShouldBe(40);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Owner chains: an owner-scoped scan visits only the owner's pages")]
    public void GetUnitIterator_OwnerScoped_ShouldVisitOnlyOwnerPages()
    {
        // Arrange: a large owner spanning many pages and a one-record owner.
        var storage = ChainStorage.Create(new CrashSimulationStream(), new CrashSimulationStream(), poolCapacity: 64);
        FillOwner(storage, ownerId: 2, "big", count: 200, recordSize: 700);

        using (var transaction = storage.BeginTransaction())
        {
            storage.Insert(transaction, 3, Text("small-row"));
            transaction.Commit();
        }

        storage.GetOwnerPages(2).Count.ShouldBeGreaterThan(10);

        // Act: scan the small owner and count the pages the iterator pins.
        long pagesVisited;
        using (var iterator = (StorageUnitIterator)storage.GetUnitIterator(3))
        {
            while (iterator.MoveNext())
            {
            }

            pagesVisited = iterator.PagesVisited;
        }

        // Assert: the scan cost is the small owner's chain, independent of the
        // large owner's size — O(object), not O(storage).
        pagesVisited.ShouldBe(storage.GetOwnerPages(3).Count);
        pagesVisited.ShouldBeLessThanOrEqualTo(2);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Owner chains: a committed release returns the chain's pages to the allocator")]
    public void FreeOwnerPages_OnCommit_ShouldReturnPagesToAllocator()
    {
        // Arrange
        var storage = ChainStorage.Create(new CrashSimulationStream(), new CrashSimulationStream(), poolCapacity: 64);
        FillOwner(storage, ownerId: 5, "victim", count: 100, recordSize: 700);

        long totalPagesBefore = storage.PageManager.PageCount;
        int chainSize = storage.GetOwnerPages(5).Count;
        chainSize.ShouldBeGreaterThan(5);

        // Act: release the chain in a committed transaction.
        int released;
        using (var transaction = storage.BeginTransaction())
        {
            released = storage.FreeOwnerPages(transaction, 5);
            transaction.Commit();
        }

        // Assert: the chain is gone, the pages are free, and a new owner's inserts
        // reuse them without growing the file.
        released.ShouldBe(chainSize);
        storage.GetOwnerPages(5).ShouldBeEmpty();
        storage.ScanText(5).ShouldBeEmpty();
        storage.FreeSpaceMap.FreePageCount.ShouldBeGreaterThanOrEqualTo(chainSize);

        FillOwner(storage, ownerId: 6, "recycled", count: 100, recordSize: 700);
        storage.PageManager.PageCount.ShouldBe(totalPagesBefore);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Owner chains: a rolled-back release restores the chain untouched")]
    public void FreeOwnerPages_OnRollback_ShouldRestoreChain()
    {
        // Arrange
        var storage = ChainStorage.Create(new CrashSimulationStream(), new CrashSimulationStream(), poolCapacity: 64);
        FillOwner(storage, ownerId: 5, "kept", count: 30, recordSize: 400);

        int chainSize = storage.GetOwnerPages(5).Count;
        long freeBefore = storage.FreeSpaceMap.FreePageCount;

        // Act
        using (var transaction = storage.BeginTransaction())
        {
            storage.FreeOwnerPages(transaction, 5);
            transaction.Rollback();
        }

        // Assert: the pages never reached the allocator and the records read back.
        storage.GetOwnerPages(5).Count.ShouldBe(chainSize);
        storage.FreeSpaceMap.FreePageCount.ShouldBe(freeBefore);
        storage.ScanText(5).Length.ShouldBe(30);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Owner chains: reopening rebuilds the directory from page headers")]
    public void OpenExisting_AfterCleanShutdown_ShouldRebuildOwnerDirectory()
    {
        // Arrange: two owners, clean shutdown (dispose checkpoints).
        var data = new CrashSimulationStream(writeThrough: true);
        var journal = new CrashSimulationStream();
        var storage = ChainStorage.Create(data, journal, poolCapacity: 64);

        FillOwner(storage, ownerId: 11, "a", count: 25, recordSize: 400);
        FillOwner(storage, ownerId: 12, "b", count: 25, recordSize: 400);
        int pagesOf11 = storage.GetOwnerPages(11).Count;
        storage.Dispose();

        // Act
        using var reopened = ChainStorage.Open(data.CaptureDurable(), journal.CaptureDurable());

        // Assert: the directory is rebuilt from headers and scoped scans work; new
        // inserts continue the rebuilt chain rather than starting a new one.
        reopened.GetOwnerPages(11).Count.ShouldBe(pagesOf11);
        reopened.ScanText(11).Length.ShouldBe(25);
        reopened.ScanText(12).Length.ShouldBe(25);

        using (var transaction = reopened.BeginTransaction())
        {
            reopened.Insert(transaction, 11, Text("post-reopen"));
            transaction.Commit();
        }

        reopened.ScanText(11).Length.ShouldBe(26);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Owner chains: a committed release survives a crash with no page flush")]
    public void Recovery_CommittedFreeWithoutPageFlush_ShouldBeRedone()
    {
        // Arrange: build a chain, make its pages durable, then release it and crash
        // with only the journal durable.
        var data = new CrashSimulationStream();
        var journal = new CrashSimulationStream();
        var storage = ChainStorage.Create(data, journal, poolCapacity: 64);

        FillOwner(storage, ownerId: 4, "doomed", count: 30, recordSize: 400);
        storage.PageManager.FlushAll();
        data.Flush();

        using (var transaction = storage.BeginTransaction())
        {
            storage.FreeOwnerPages(transaction, 4);
            transaction.Commit();
        }

        // Act: crash — the freed page images live only in the journal.
        using var recovered = ChainStorage.Open(data.CaptureDurable(), journal.CaptureDurable());

        // Assert: recovery replays the release; the chain is gone and its pages are free.
        recovered.GetOwnerPages(4).ShouldBeEmpty();
        recovered.ScanText(4).ShouldBeEmpty();
        recovered.FreeSpaceMap.FreePageCount.ShouldBeGreaterThan(0);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Owner chains: an uncommitted release is undone by recovery")]
    public void Recovery_UncommittedFreeWithStolenWrites_ShouldRestoreChain()
    {
        // Arrange: worst-case steal — the retyped Free pages reach the data file
        // before the release ever commits.
        var data = new CrashSimulationStream(writeThrough: true);
        var journal = new CrashSimulationStream();
        var storage = ChainStorage.Create(data, journal, poolCapacity: 64);

        FillOwner(storage, ownerId: 8, "survivor", count: 30, recordSize: 400);

        var transaction = storage.BeginTransaction();
        storage.FreeOwnerPages(transaction, 8);
        storage.PageManager.FlushAll(); // steal: freed images hit the data stream

        // Act: crash without committing.
        using var recovered = ChainStorage.Open(data.CaptureDurable(), journal.CaptureDurable());

        // Assert: the before-images restore the chain — records and directory intact.
        recovered.ScanText(8).Length.ShouldBe(30);
        recovered.GetOwnerPages(8).ShouldNotBeEmpty();
    }
}
