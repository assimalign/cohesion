using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage.Units;

namespace Assimalign.Cohesion.Database.Storage.Tests;

/// <summary>
/// The memory bound of a storage transaction's pre-images (#1253): a pre-image is kept as its
/// encoded non-zero runs, so a page the transaction allocated costs a few dozen bytes; past the
/// pre-image budget the rest spill to the journal as full page images, read back for the commit's
/// deltas or the rollback.
/// </summary>
public sealed class StoragePreImageTests
{
    private const int Pool = 128;
    private const int TenPools = 10 * Pool;

    /// <summary>
    /// An index build in one bracket allocates and fills page after page: ten times the pool here.
    /// Each pre-image is a freshly initialized page, so the bracket holds a few dozen bytes per page
    /// where it used to hold 8 KiB, and spills nothing.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Storage] - Pre-images: a bracket that allocates ten times the pool keeps compact pre-images and spills none")]
    public void Commit_IndexBuildTouchingTenTimesThePool_ShouldKeepCompactPreImages()
    {
        // Arrange: a 128-page (1 MiB) pool; its default budget is the 16 MiB minimum.
        var storage = MemoryStorage.Create(Pool); // abandoned: a process crash
        var pages = new List<PageId>();

        // Act: one bracket builds 1,280 index-like pages, about half full.
        using (var transaction = storage.BeginTransaction())
        {
            for (int i = 0; i < TenPools; i++)
            {
                using var handle = storage.AllocatePageForWrite(transaction, PageType.Index);
                Fill(handle.Page.AsBodySpan()[..4000], seed: i);
                handle.MarkDirty();
                pages.Add(handle.Id);
            }

            transaction.Commit();
        }

        long peak = storage.PeakPreImageBytes;
        long spilled = storage.SpilledPreImages;
        using var recovered = MemoryStorage.Open(storage.Capture(), Pool);

        // Assert: under 2% of the 10 MiB that full copies of the pre-images cost.
        storage.PreImageBudget.ShouldBe(Storage.MinimumPreImageBudget);
        spilled.ShouldBe(0L);
        peak.ShouldBeLessThan(TenPools * (long)Page.Size / 50, $"peak {peak} bytes");
        storage.PreImageBytes.ShouldBe(0L);
        foreach (int i in new[] { 0, 1, TenPools / 2, TenPools - 1 })
        {
            var expected = new byte[4000];
            Fill(expected, seed: i);
            recovered.PageBytes(pages[i]).AsSpan(Page.HeaderSize, 4000).ToArray().ShouldBe(expected);
        }
    }

    /// <summary>
    /// A bracket that changes ten times the pool of existing, full pages: past the budget each
    /// first touch journals the page's full image and the bracket keeps only where it lies, so its
    /// pre-images never hold more than the budget, and the commit reads them back for its deltas.
    /// </summary>
    [Theory(DisplayName = "Cohesion Test [Storage] - Pre-images: a bracket changing ten times the pool of full pages spills past the budget and commits or rolls back")]
    [InlineData(true)]
    [InlineData(false)]
    public void Bracket_TouchingTenTimesThePoolOfFullPages_ShouldStayWithinTheBudget(bool commit)
    {
        // Arrange: 1,280 pages, each holding one 7,000-byte record, all imaged in this checkpoint
        // interval, so no first touch below needs an image of its own. Their 9 MB of pre-images
        // would fit the default 16 MiB budget; a budget of the pool's size (1 MiB) stands in for a
        // bracket larger than the default.
        var storage = MemoryStorage.Create(Pool); // abandoned: a process crash
        storage.PreImageBudget = (long)Pool * Page.Size;
        var rows = new List<(PageId PageId, int Slot)>();
        for (int batch = 0; batch < TenPools / 64; batch++)
        {
            using var transaction = storage.BeginTransaction();
            for (int i = 0; i < 64; i++)
            {
                rows.Add(storage.Insert(transaction, 7, Record(rows.Count, version: 0)));
            }

            transaction.Commit();
        }

        long spilledBefore = storage.SpilledPreImages;
        long budget = storage.PreImageBudget;

        // Act
        using (var transaction = storage.BeginTransaction())
        {
            for (int i = 0; i < rows.Count; i++)
            {
                storage.Update(transaction, rows[i].PageId, rows[i].Slot, Record(i, version: 1));
            }

            if (commit)
            {
                transaction.Commit();
            }
            else
            {
                transaction.Rollback();
            }
        }

        long spilled = storage.SpilledPreImages - spilledBefore;
        long peak = storage.PeakPreImageBytes;
        using var recovered = MemoryStorage.Open(storage.Capture(), Pool);

        // Assert
        peak.ShouldBeLessThanOrEqualTo(budget);
        spilled.ShouldBeGreaterThan(TenPools - (budget / 7000));
        int version = commit ? 1 : 0;
        for (int i = 0; i < rows.Count; i += 37)
        {
            storage.ReadBytes(rows[i].PageId, rows[i].Slot).ShouldBe(Record(i, version), $"row {i} in memory");
            recovered.ReadBytes(rows[i].PageId, rows[i].Slot).ShouldBe(Record(i, version), $"row {i} recovered");
        }
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Pre-images: a spilled pre-image reads back from the append buffer and from the journal file")]
    [InlineData(false)]
    [InlineData(true)]
    public void Rollback_SpilledPreImage_ShouldReadBackWhereverItsFrameIs(bool drained)
    {
        // Arrange: a budget of one byte spills every pre-image.
        using var storage = TornStorage.Create();
        var (pageId, slot) = storage.Insert("before");
        storage.PreImageBudget = 1;
        var transaction = storage.BeginTransaction();
        storage.Update(transaction, pageId, slot, "after!");
        if (drained)
        {
            storage.Log.Flush();
        }

        // Act
        transaction.Rollback();

        // Assert
        storage.SpilledPreImages.ShouldBe(1L);
        storage.Read(pageId, slot).ShouldBe("before");
        using var next = storage.BeginTransaction();
        storage.Update(next, pageId, slot, "again!");
        next.Commit();
        storage.Read(pageId, slot).ShouldBe("again!");
    }

    private static byte[] Record(int row, int version)
    {
        var record = new byte[7000];
        new Random((row * 31) + version).NextBytes(record);
        return record;
    }

    private static void Fill(Span<byte> span, int seed)
    {
        var random = new Random(seed);
        for (int i = 0; i < span.Length; i++)
        {
            span[i] = (byte)random.Next(1, 256);
        }
    }

    /// <summary>
    /// A storage over memory streams. <see cref="Capture"/> takes what a process crash leaves: every
    /// byte written to either file, and nothing still in the journal's append buffer. Unlike the
    /// crash-simulation streams it does not copy a file per write, which ten pools' worth of pages
    /// would make take minutes.
    /// </summary>
    private sealed class MemoryStorage : Storage
    {
        private readonly MemoryStream _data;
        private readonly MemoryStream _journal;

        private MemoryStorage(MemoryStream data, MemoryStream journal, int pool)
            : base(StorageModel.Custom, 
                new StorageStream(new SimulatedDurableFileHandle(data)),
                new StorageStream(new SimulatedDurableFileHandle(journal)),
                new StorageStream(new MemoryStream()),
                pool)
        {
            _data = data;
            _journal = journal;
        }


        public static MemoryStorage Create(int pool)
        {
            var storage = new MemoryStorage(new MemoryStream(), new MemoryStream(), pool);
            storage.InitializeNew((Name)"pre-images");
            return storage;
        }

        public static MemoryStorage Open((byte[] Data, byte[] Journal) images, int pool)
        {
            var data = new MemoryStream();
            data.Write(images.Data);
            var journal = new MemoryStream();
            journal.Write(images.Journal);
            var storage = new MemoryStorage(data, journal, pool);
            storage.OpenExisting();
            return storage;
        }

        public (byte[] Data, byte[] Journal) Capture() => (_data.ToArray(), _journal.ToArray());

        public (PageId PageId, int SlotIndex) Insert(StorageTransaction transaction, ulong owner, byte[] record)
            => InsertRecord(transaction, owner, record);

        public void Update(StorageTransaction transaction, PageId pageId, int slotIndex, byte[] record)
            => UpdateRecord(transaction, pageId, slotIndex, record);

        public byte[] ReadBytes(PageId pageId, int slotIndex) => ReadRecord(pageId, slotIndex).ToArray();

        public unsafe byte[] PageBytes(PageId pageId)
        {
            using var handle = PageManager.GetPage(pageId);
            return new ReadOnlySpan<byte>(handle.Page.Pointer, Page.Size).ToArray();
        }
    }
}
