using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Indexing.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Indexing.Tests;

/// <summary>Deterministic allocation and page-work bounds for short consumption of wide ranges.</summary>
[Collection(TimingCollection.Name)]
public sealed class BTreeCursorAllocationTests
{
    private readonly ITestOutputHelper _output;

    /// <summary>Creates the measurement fixture.</summary>
    /// <param name="output">The test output receiving local measurements.</param>
    public BTreeCursorAllocationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - Cursor: ten results from a million keys require bounded allocation (#1542)")]
    public async Task OpenCursor_MillionKeysAndTenResults_ShouldAllocateOneBatch()
    {
        // Arrange: committed build stamps, with bounded physical write brackets.
        await using var harness = new IndexTestHarness();
        var setup = await harness.BeginAsync();
        var index = await harness.IndexManager.CreateIndexAsync(setup, 1, new IndexDefinition("ix_cursor_allocation"));
        await harness.CommitAsync(setup);
        for (int first = 0; first < 1_000_000; first += 10_000)
        {
            using var build = harness.Storage.BeginTransaction();
            for (int i = first; i < first + 10_000; i++)
            {
                await index.InsertVersionAsync(build, IndexKey.FromInt64(i), (ulong)i, setup.Sequence, TransactionSequence.None);
            }
            build.Commit();
        }
        var reader = await harness.Manager.BeginAsync();
        var smallRange = new IndexKeyRange(null, IndexKey.FromInt64(10_000));
        Measure(index, reader.Snapshot, smallRange);
        Measure(index, reader.Snapshot, IndexKeyRange.All);

        // Act: minimum of three rounds excludes collection and instrumentation noise.
        long small = long.MaxValue;
        long large = long.MaxValue;
        for (int round = 0; round < 3; round++)
        {
            small = Math.Min(small, Measure(index, reader.Snapshot, smallRange));
            large = Math.Min(large, Measure(index, reader.Snapshot, IndexKeyRange.All));
        }

        // Assert
        _output.WriteLine($"10 results: 10,000-key range = {small:N0} bytes; 1,000,000-key range = {large:N0} bytes.");
        large.ShouldBeLessThan(20_000);
        large.ShouldBeLessThanOrEqualTo(small + 1_024);

        foreach (bool reverse in new[] { false, true })
        {
            await using var cursor = index.OpenCursor(reader.Snapshot, IndexKeyRange.All, reverse);
            cursor.PagesVisited.ShouldBe(0);
            cursor.EntriesExamined.ShouldBe(0);
            for (int i = 0; i < 10; i++)
            {
                (await cursor.MoveNextAsync(CancellationToken.None)).ShouldBeTrue();
                cursor.CurrentEntryReference.ShouldBe(reverse ? 999_999UL - (ulong)i : (ulong)i);
            }
            cursor.PagesVisited.ShouldBe(8); // Height 3 + leaf reopened, for the 1-entry probe and 64-entry batch.
            cursor.EntriesExamined.ShouldBe(BTreeCursor.BatchSize + 1);
            _output.WriteLine($"{(reverse ? "reverse" : "forward")}: {cursor.PagesVisited} page visits, {cursor.EntriesExamined} examined entries.");
        }
        await harness.Manager.CommitAsync(reader);
    }

    private static long Measure(BTreeIndex index, TransactionSnapshot snapshot, IndexKeyRange range)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        var cursor = index.OpenCursor(snapshot, range);
        try
        {
            for (int i = 0; i < 10; i++)
            {
                cursor.MoveNextAsync(CancellationToken.None).GetAwaiter().GetResult().ShouldBeTrue();
                cursor.CurrentEntryReference.ShouldBe((ulong)i);
            }
        }
        finally { cursor.DisposeAsync().GetAwaiter().GetResult(); }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
