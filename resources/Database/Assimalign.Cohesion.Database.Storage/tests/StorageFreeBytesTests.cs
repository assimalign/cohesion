using System;
using System.Collections.Generic;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Assimalign.Cohesion.Database.Storage.Tests;

public sealed partial class StorageFreeBytesTests
{
    private const ulong owner = 7;
    private readonly ITestOutputHelper _output;
    public StorageFreeBytesTests(ITestOutputHelper output) => _output = output;

    [Fact(DisplayName = "Cohesion Test [Storage] - Free bytes: a small insert revisits a page passed over by a large record (#1288)")]
    public void InsertRecord_OlderPageHasContiguousSpace_ShouldReuseIt()
    {
        // Arrange
        using var storage = TornStorage.Create();
        var first = Insert(storage, Filled(2000, 0x11));
        var large = Insert(storage, Filled(6500, 0x33));
        large.PageId.ShouldNotBe(first.PageId);
        long pages = storage.FreeSpaceMap.TotalPageCount;

        // Act
        var next = Insert(storage, Filled(2000, 0x77));

        // Assert
        next.PageId.ShouldBe(first.PageId);
        storage.FreeSpaceMap.TotalPageCount.ShouldBe(pages);
        storage.ReadBytes(large.PageId, large.SlotIndex).ShouldBe(Filled(6500, 0x33));
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Free bytes: deleted space on an older page is compacted before extension (#1288)")]
    public void InsertRecord_OlderPageIsFragmented_ShouldCompactAndReuseIt()
    {
        // Arrange
        using var storage = TornStorage.Create();
        var deleted = Insert(storage, Filled(4000, 0x11));
        var kept = Insert(storage, Filled(3000, 0x33));
        kept.PageId.ShouldBe(deleted.PageId);
        Insert(storage, Filled(7000, 0x55)).PageId.ShouldNotBe(kept.PageId);
        using (var purge = storage.BeginTransaction())
        {
            storage.Delete(purge, deleted.PageId, deleted.SlotIndex);
            purge.Commit();
        }
        long pages = storage.FreeSpaceMap.TotalPageCount;

        // Act
        var next = Insert(storage, Filled(1500, 0x77));

        // Assert
        next.PageId.ShouldBe(kept.PageId);
        next.SlotIndex.ShouldBe(2);
        storage.FreeSpaceMap.TotalPageCount.ShouldBe(pages);
        storage.ReadBytes(kept.PageId, kept.SlotIndex).ShouldBe(Filled(3000, 0x33));
        storage.TryReadRecord(deleted.PageId, deleted.SlotIndex, owner, out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Free bytes: fixed live data with overwrite and purge stops extending the file (#1288)")]
    public void InsertRecord_OverwriteAndPurge_ShouldReachAStableFileSize()
    {
        // Arrange: sixteen 500-byte records per page; purge leaves older pages partly full.
        using var storage = TornStorage.Create();
        var locations = new (PageId PageId, int SlotIndex)[1024];
        var values = new byte[locations.Length];
        using (var seed = storage.BeginTransaction())
        {
            for (int i = 0; i < locations.Length; i++)
            {
                values[i] = (byte)(i % 251);
                locations[i] = storage.Insert(seed, owner, Filled(500, values[i]));
            }
            seed.Commit();
        }
        long initial = storage.FreeSpaceMap.TotalPageCount;
        var counts = new List<long>();

        // Act: visit six distinct records of each original page; never empty an old page.
        for (int cycle = 0; cycle < 384; cycle++)
        {
            int index = ((cycle % 64) * 16) + (cycle / 64);
            using var bracket = storage.BeginTransaction();
            var old = locations[index];
            values[index] = (byte)((cycle % 251) + 1);
            locations[index] = storage.Insert(bracket, owner, Filled(500, values[index]));
            storage.Delete(bracket, old.PageId, old.SlotIndex);
            bracket.Commit();
            if ((cycle + 1) % 32 == 0) { counts.Add(storage.FreeSpaceMap.TotalPageCount); }
        }

        // Assert: one initial slack page, followed by twelve constant page-count samples.
        _output.WriteLine($"Initial pages: {initial}; after each 32 replacements: {string.Join(", ", counts)}");
        counts.Count.ShouldBe(12);
        counts.ShouldAllBe(count => count == initial + 1);
        storage.Data.Length.ShouldBe((initial + 1) * Units.Page.Size);
        for (int i = 0; i < locations.Length; i++)
        {
            storage.ReadBytes(locations[i].PageId, locations[i].SlotIndex).ShouldBe(Filled(500, values[i]));
        }
    }

    private static (PageId PageId, int SlotIndex) Insert(TornStorage storage, byte[] bytes)
    {
        using var bracket = storage.BeginTransaction();
        var result = storage.Insert(bracket, owner, bytes);
        bracket.Commit();
        return result;
    }

    private static byte[] Filled(int count, byte value)
    {
        byte[] bytes = new byte[count];
        bytes.AsSpan().Fill(value);
        return bytes;
    }
}
