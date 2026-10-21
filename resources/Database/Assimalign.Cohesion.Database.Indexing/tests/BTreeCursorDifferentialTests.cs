using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Indexing.Internal;
using Assimalign.Cohesion.Database.Indexing.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Indexing.Tests;

/// <summary>Seeded, boundary-driven continuation proofs; thread races are supplementary.</summary>
[Collection(BTreeRollbackRaceCollection.Name)]
public sealed class BTreeCursorDifferentialTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Indexing] - Cursor: every batch boundary mutation agrees with the pinned-set oracle (#1542)")]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(64, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(64, true)]
    public async Task MoveNextAsync_SeededBoundaryMutations_ShouldEqualSnapshotOracle(int batchSize, bool reverse)
    {
        // Arrange: random bounds, all endpoint inclusivity combinations, and a shuffled
        // mutation deck. Fixed seeds make failures reproducible on every platform.
        var random = new Random(1542);
        var exercised = new HashSet<int>();
        for (int scenario = 0; scenario < 8; scenario++)
        {
            await using var harness = new IndexTestHarness();
            var seed = await harness.BeginAsync();
            var index = await harness.IndexManager.CreateIndexAsync(seed, 1, new IndexDefinition("differential"));
            var visible = new List<(IndexKey Key, ulong Reference)>();
            for (int i = 0; i < 160; i++)
            {
                var entry = (Key(i / 10), (ulong)i);
                visible.Add(entry);
                await index.InsertAsync(seed, entry.Item1, entry.Item2);
            }
            await harness.CommitAsync(seed);
            var reader = await harness.Manager.BeginAsync();
            int low = random.Next(0, 4);
            int high = random.Next(12, 16);
            var range = new IndexKeyRange(Key(low), Key(high), (scenario & 1) != 0, (scenario & 2) != 0);
            var expected = visible.Where(entry =>
                (entry.Key.CompareTo(range.Start!.Value) > 0 || range.IsStartInclusive && entry.Key.Equals(range.Start.Value)) &&
                (entry.Key.CompareTo(range.End!.Value) < 0 || range.IsEndInclusive && entry.Key.Equals(range.End.Value))).ToList();
            if (reverse) { expected.Reverse(); }
            await using var cursor = index.OpenCursor(reader.Snapshot, range, reverse);
            cursor.BatchSizeOverride = batchSize;
            int[] deck = Enumerable.Range(0, 7).ToArray();
            random.Shuffle(deck);
            // Rotate starts too, so the large-batch cases exercise every operation.
            int boundary = scenario * 2;
            ulong nextReference = 10_000;
            bool rootGrown = false;
            var actual = new List<(IndexKey Key, ulong Reference)>();
            cursor.BatchRead = () => MutateAsync().GetAwaiter().GetResult();

            // Act / Assert: fail immediately on an extra/repeated entry (also bounds
            // a broken strict-continuation comparison instead of hanging the test).
            while (await cursor.MoveNextAsync())
            {
                actual.Count.ShouldBeLessThan(expected.Count);
                actual.Add((cursor.CurrentKey, cursor.CurrentEntryReference));
                actual[^1].ShouldBe(expected[actual.Count - 1]);
            }
            actual.ShouldBe(expected);
            await harness.Manager.CommitAsync(reader);

            async Task MutateAsync()
            {
                boundary.ShouldBeLessThan(1_000, $"scenario {scenario}, continuation {cursor.Continuation}, fence {cursor.Boundary}");
                int mutation = deck[boundary++ % deck.Length];
                exercised.Add(mutation);
                var last = cursor.Continuation;
                var near = last is { } identity ? new IndexKey(identity.Key) : Key(low);
                switch (mutation)
                {
                    case 0: // Duplicate keys on both sides of the logical continuation.
                    {
                        var writer = await harness.BeginAsync();
                        await index.InsertAsync(writer, near, 0);
                        await index.InsertAsync(writer, near, nextReference++);
                        await harness.CommitAsync(writer);
                        break;
                    }
                    case 1: // Same key/reference, distinct invisible writer tiebreaker.
                    {
                        var writer = await harness.BeginAsync();
                        await index.InsertAsync(writer, near, last?.EntryReference ?? 7);
                        await harness.CommitAsync(writer);
                        break;
                    }
                    case 2: // Abort after a durable bracket, then purge its entries.
                    {
                        var writer = await harness.BeginAsync();
                        await index.InsertAsync(writer, near, nextReference++);
                        await harness.LogicalRollbackAsync(writer, bracket =>
                        {
                            index.PurgeWriters(bracket, new HashSet<TransactionSequence> { writer.Sequence }).ShouldBe(1);
                            return Task.CompletedTask;
                        });
                        break;
                    }
                    case 3: // Erase the exact continuation, including a snapshot-visible
                            // identity already copied into this batch's owned buffer.
                    {
                        if (last is not { } entry) { break; }
                        using var erase = harness.Storage.BeginTransaction();
                        await index.EraseAsync(erase, new IndexKey(entry.Key), entry.EntryReference, new TransactionSequence(entry.Writer));
                        erase.Commit();
                        break;
                    }
                    case 4: // Grow through root/internal splits, away from the visible set.
                    case 5: // The same split work, physically rolled back before resumption.
                    {
                        int height = Height(harness, index);
                        var writer = await harness.BeginAsync();
                        int inserted = 0;
                        do
                        {
                            for (int i = 0; i < 240; i++)
                            { await index.InsertAsync(writer, SplitKey(100 + inserted++), nextReference++); }
                            inserted.ShouldBeLessThan(5_000);
                        }
                        while (mutation == 4 && !rootGrown && Height(harness, index) == height);
                        if (mutation == 4)
                        {
                            if (!rootGrown) { Height(harness, index).ShouldBeGreaterThan(height); rootGrown = true; }
                            await harness.CommitAsync(writer);
                        }
                        else
                        {
                            await harness.RollbackAsync(writer);
                            Height(harness, index).ShouldBe(height);
                        }
                        break;
                    }
                    case 6: // Create and erase entire invisible leaves inside the range.
                    {
                        var writer = await harness.BeginAsync();
                        // Reuse the finite slot range, as storage does after purge. Growing
                        // a new empty chain at every empty-page boundary could starve any scan.
                        for (int i = 0; i < 48; i++) { await index.InsertAsync(writer, Key(low), 2_000UL + (ulong)i); }
                        await harness.LogicalRollbackAsync(writer, bracket =>
                        {
                            index.PurgeWriters(bracket, new HashSet<TransactionSequence> { writer.Sequence }).ShouldBe(48);
                            return Task.CompletedTask;
                        });
                        break;
                    }
                }
            }
        }
        exercised.Order().ShouldBe(Enumerable.Range(0, 7));
    }

    [Theory(DisplayName = "Cohesion Test [Database.Indexing] - Cursor: Entry separators fence whole erased leaves of a reused-reference run (#1542)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MoveNextAsync_EntryFencedEmptyLeaves_ShouldKeepWriterOrder(bool reverse)
    {
        // Arrange: all pivots inside this run need both reference and writer.
        await using var harness = new IndexTestHarness();
        var setup = await harness.BeginAsync();
        var index = await harness.IndexManager.CreateIndexAsync(setup, 1, new IndexDefinition("entry_fences"));
        await harness.CommitAsync(setup);
        var key = Key(0);
        using (var build = harness.Storage.BeginTransaction())
        {
            for (ulong writer = 1; writer <= 600; writer++)
            { await index.InsertVersionAsync(build, key, 7, new TransactionSequence(writer), TransactionSequence.None); }
            build.Commit();
        }
        var expected = new List<ulong>();
        using (var erase = harness.Storage.BeginTransaction())
        {
            for (ulong writer = 1; writer <= 600; writer++)
            {
                if (writer is >= 20 and <= 580)
                { await index.EraseAsync(erase, key, 7, new TransactionSequence(writer)); }
                else { expected.Add(writer); }
            }
            erase.Commit();
        }
        if (reverse) { expected.Reverse(); }
        var snapshot = new TransactionSnapshot(new TransactionSequence(1_000), TransactionSequence.None,
            new TransactionSequence(1_000), []);
        await using var cursor = index.OpenCursor(snapshot, IndexKeyRange.All, reverse);
        cursor.BatchSizeOverride = 1;
        int emptyFences = 0;
        cursor.BatchRead = () =>
        {
            if (cursor.Boundary is { } fence)
            { fence.Tiebreaker.ShouldBe(BTreeTiebreaker.Entry); emptyFences++; }
        };
        var actual = new List<ulong>();

        // Act
        while (await cursor.MoveNextAsync()) { actual.Add(cursor.Continuation!.Value.Writer); }

        // Assert
        actual.ShouldBe(expected);
        emptyFences.ShouldBeGreaterThan(10);
    }

    private static IndexKey Key(int value)
    {
        byte[] bytes = new byte[508];
        bytes.AsSpan(0, 500).Fill(46);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(500), value);
        return new IndexKey(bytes);
    }

    private static IndexKey SplitKey(int value)
    {
        byte[] bytes = new byte[1_008];
        bytes.AsSpan(0, 1_000).Fill(46);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(1_000), value);
        return new IndexKey(bytes);
    }

    private static int Height(IndexTestHarness harness, BTreeIndex index)
    {
        int height = 0;
        long pageId = index.RootPageId;
        while (true)
        {
            using var handle = harness.Storage.PageManager.GetPage((PageId)pageId);
            var node = new BTreeNode(handle.Page.AsBodySpan());
            height++;
            if (node.IsLeaf) { return height; }
            pageId = node.LeftmostChild;
        }
    }
}
