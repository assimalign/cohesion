using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Indexing.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Indexing.Tests;

public sealed class BTreeShapeTests
{
    private readonly ITestOutputHelper _output;

    public BTreeShapeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory(DisplayName = "Cohesion Test [Database.Indexing] - Split: deterministic duplicate, ascending and random tree shapes (#1196)")]
    [InlineData("duplicates", 1196)]
    [InlineData("ascending", 1196)]
    [InlineData("random-duplicates", 1196)]
    public async Task Insert_OrderedAndRandomKeys_ShouldKeepLeavesDense(string order, int seed)
    {
        // Arrange / Act
        var shape = await MeasureAsync(order, seed);

        // Assert: baseline midpoint splits needed 1,666 ordered leaves; shuffled
        // references needed 1,012. Heights include the leaf level.
        shape.LeafCounts.Count.ShouldBeLessThanOrEqualTo(order == "random-duplicates" ? 1_012 : 834);
        shape.Fill.ShouldBeGreaterThanOrEqualTo(order == "random-duplicates" ? 0.64 : 0.78);
        shape.Height.ShouldBeLessThanOrEqualTo(order == "ascending" ? 3 : order == "random-duplicates" ? 5 : 6);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - Split: random tree fill and height do not regress across fixed seeds (#1196)")]
    public async Task Insert_ShuffledKeys_ShouldNotIncreaseTotalLeafPages()
    {
        // Arrange
        int leaves = 0;

        // Act: several permutations avoid treating a one-page split-boundary
        // variation in one small tree as a change in random-insert utilization.
        foreach (int seed in new[] { 1196, 77, 42 })
        {
            var shape = await MeasureAsync("random", seed);
            leaves += shape.LeafCounts.Count;
            shape.Height.ShouldBe(3);
        }

        // Assert: the midpoint baseline used 1,019 + 1,019 + 1,014 pages.
        leaves.ShouldBeLessThanOrEqualTo(3_052);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - Split: shuffled duplicate pairs stay within 0.5% aggregate and 1% per seed of midpoint (#1196)")]
    public async Task Insert_ShuffledDuplicatePairs_ShouldStayWithinMidpointTolerance()
    {
        // Arrange
        int leaves = 0;
        int midpointLeaves = 0;

        // Act
        foreach (var (seed, baselineLeaves) in new[] { (1196, 1_037), (77, 1_047), (42, 1_053) })
        {
            var shape = await MeasureAsync("random-pairs", seed);
            leaves += shape.LeafCounts.Count;
            midpointLeaves += baselineLeaves;
            shape.Height.ShouldBe(4);
            shape.LeafCounts.Count.ShouldBeLessThanOrEqualTo((int)Math.Floor(baselineLeaves * 1.01), $"seed {seed}: at most 1% above midpoint");
        }

        // Assert: the accepted 3,144 leaves are 0.223% above midpoint's 3,137.
        leaves.ShouldBeLessThanOrEqualTo((int)Math.Floor(midpointLeaves * 1.005));
    }

    [Theory(DisplayName = "Cohesion Test [Database.Indexing] - Split: new references for existing keys retain midpoint utilization (#1196)")]
    [InlineData("random-load-random-updates", 1_024, 2_095)]
    [InlineData("random-load-ascending-updates", 1_024, 2_228)]
    [InlineData("random-load-random-updates", 500, 967)]
    [InlineData("random-load-ascending-updates", 500, 1_075)]
    [InlineData("random-load-random-updates", 52, 139)]
    [InlineData("random-load-ascending-updates", 52, 139)]
    [InlineData("random-updates", 52, 100)]
    [InlineData("ascending-updates", 52, 100)]
    public async Task Insert_NewReferenceForEveryKey_ShouldStayWithinMidpointTolerance(string order, int keyLength, int midpointLeaves)
    {
        // Arrange / Act: 5,000 keys, followed by 5,000 new references.
        var shape = await MeasureAsync(order, 1196, keyLength);

        // Assert: random initial loads at every size and 52-byte ascending loads
        // stay within 5% of midpoint. Larger ascending loads have separate guards.
        shape.LeafCounts.Count.ShouldBeLessThanOrEqualTo((int)Math.Floor(midpointLeaves * 1.05));
    }

    [Theory(DisplayName = "Cohesion Test [Database.Indexing] - Split: updates after ascending loads do not exceed the run-end regression (#1196)")]
    [InlineData("random-updates", 1_024, 2_580)]
    [InlineData("ascending-updates", 1_024, 2_500)]
    [InlineData("random-updates", 500, 1_299)]
    [InlineData("ascending-updates", 500, 1_429)]
    public async Task Insert_NewReferencesAfterAscendingLoad_ShouldNotExceedRunEndRegression(string order, int keyLength, int regressionLeaves)
    {
        // Arrange / Act: densely pack 5,000 ascending keys, then add a new
        // reference for every key. MeasureAsync also reports the load's leaves.
        var shape = await MeasureAsync(order, 1196, keyLength);
        _output.WriteLine($"Ascending load plus updates: order={order}, keyLength={keyLength}, totalLeaves={shape.LeafCounts.Count}");

        // Assert: sequential packing trades update headroom for fewer load pages.
        // The accepted ceiling is the same case at 7a4d055ed, not midpoint's
        // half-empty initial tree (DESIGN.md, Update workloads after sequential loads).
        shape.LeafCounts.Count.ShouldBeLessThanOrEqualTo(regressionLeaves);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Indexing] - Split: descending and alternating shapes retain their packing (#1196)")]
    [InlineData("descending", 1_250)]
    [InlineData("zig-zag", 1_042)]
    [InlineData("alternating-extremes", 1_458)]
    [InlineData("descending-pairs", 1_250)]
    public async Task Insert_AlternatingAndDescendingKeys_ShouldRetainPacking(string order, int maximumLeaves)
    {
        // Arrange / Act
        var shape = await MeasureAsync(order, 1196);

        // Assert
        shape.LeafCounts.Count.ShouldBeLessThanOrEqualTo(maximumLeaves);
    }

    private async Task<BTreeShape> MeasureAsync(string order, int seed, int keyLength = BTreeIndex.MaxKeyLength)
    {
        // Arrange
        await using var harness = new IndexTestHarness();
        var setup = await harness.BeginAsync();
        var index = await harness.IndexManager.CreateIndexAsync(setup, 1, new IndexDefinition("ix_shape", IndexKind.BTree));
        await harness.CommitAsync(setup);
        var values = Enumerable.Range(0, 5_000).ToArray();
        bool updates = order.EndsWith("updates", StringComparison.Ordinal);
        if (order.StartsWith("random", StringComparison.Ordinal) && (!updates || order.StartsWith("random-load", StringComparison.Ordinal)))
        {
            new Random(seed).Shuffle(values);
        }
        else if (order == "descending")
        {
            Array.Reverse(values);
        }
        else if (order == "descending-pairs")
        {
            values = values.Select(value => 4_998 - value / 2 * 2 + value % 2).ToArray();
        }
        else if (order == "zig-zag")
        {
            values = values.Select(value => value % 2 == 0 ? 2_500 + value / 2 : 2_499 - value / 2).ToArray();
        }
        else if (order == "alternating-extremes")
        {
            values = values.Select(value => value % 2 == 0 ? value / 2 : 4_999 - value / 2).ToArray();
        }
        var entries = new List<(long Key, ulong Reference)>();

        // Act
        var writer = await harness.BeginAsync();
        foreach (int value in values)
        {
            long key = order.Contains("duplicates", StringComparison.Ordinal) ? 0
                : order == "random-pairs" ? value % 2_500
                : order == "descending-pairs" ? value / 2 : value;
            await index.InsertAsync(writer, PaddedKey(key, keyLength), (ulong)value, CancellationToken.None);
            entries.Add((key, (ulong)value));
        }
        await harness.CommitAsync(writer);
        if (updates)
        {
            var loaded = BTreeShape.Read(harness.Storage, harness.IndexManager.ExportRegistrations().Single().RootPageId);
            _output.WriteLine($"{order} load, keyLength={keyLength}: leaves={loaded.LeafCounts.Count}, fill={loaded.Fill:P3}, height={loaded.Height}");
            if (order.EndsWith("random-updates", StringComparison.Ordinal))
            {
                new Random(seed + 1).Shuffle(values);
            }
            else
            {
                Array.Sort(values);
            }
            writer = await harness.BeginAsync();
            foreach (int value in values)
            {
                ulong reference = (ulong)(value + values.Length);
                await index.InsertAsync(writer, PaddedKey(value, keyLength), reference, CancellationToken.None);
                entries.Add((value, reference));
            }
            await harness.CommitAsync(writer);
        }
        var shape = BTreeShape.Read(harness.Storage, harness.IndexManager.ExportRegistrations().Single().RootPageId);
        _output.WriteLine($"{order}, seed={seed}, keyLength={keyLength}: leaves={shape.LeafCounts.Count}, entries={shape.LeafCounts.Sum()}, fill={shape.Fill:P3}, height={shape.Height}");

        // Assert
        shape.LeafCounts.Sum().ShouldBe(entries.Count);
        var expectedEntries = entries.OrderBy(entry => entry.Key).ThenBy(entry => entry.Reference).ToArray();
        var reader = await harness.BeginAsync();
        await using var cursor = index.OpenCursor(reader, IndexKeyRange.All);
        int expected = 0;
        while (await cursor.MoveNextAsync(CancellationToken.None))
        {
            expected.ShouldBeLessThan(expectedEntries.Length);
            cursor.CurrentKey.ShouldBe(PaddedKey(expectedEntries[expected].Key, keyLength));
            cursor.CurrentEntryReference.ShouldBe(expectedEntries[expected++].Reference);
        }
        expected.ShouldBe(expectedEntries.Length);
        await harness.RollbackAsync(reader);
        return shape;
    }

    [Theory(DisplayName = "Cohesion Test [Database.Indexing] - Split: a run sharing its leaf with higher keys retains the midpoint through both insertion paths (#1196)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Insert_MixedRunBeforeHigherKeys_ShouldKeepMidpointSplit(bool build)
    {
        // Arrange: neighbours remain on both sides. The run's last leaf initially
        // also contains the higher key, so its insertion slot is inside that leaf.
        await using var harness = new IndexTestHarness();
        var setup = await harness.BeginAsync();
        var index = await harness.IndexManager.CreateIndexAsync(setup, 1, new IndexDefinition("ix_run", IndexKind.BTree));
        await index.InsertAsync(setup, PaddedKey(49), 9_000, CancellationToken.None);
        await index.InsertAsync(setup, PaddedKey(51), 9_001, CancellationToken.None);
        await harness.CommitAsync(setup);

        // Act
        if (build)
        {
            using var bracket = harness.Storage.BeginTransaction();
            for (ulong i = 0; i < 5_000; i++)
            {
                await index.InsertVersionAsync(bracket, PaddedKey(50), i, setup.Sequence, TransactionSequence.None, CancellationToken.None);
            }
            bracket.Commit();
        }
        else
        {
            var writer = await harness.BeginAsync();
            for (ulong i = 0; i < 5_000; i++)
            {
                await index.InsertAsync(writer, PaddedKey(50), i, CancellationToken.None);
            }
            await harness.CommitAsync(writer);
        }
        var shape = BTreeShape.Read(harness.Storage, harness.IndexManager.ExportRegistrations().Single().RootPageId);
        _output.WriteLine($"bounded run: leaves={shape.LeafCounts.Count}, fill={shape.Fill:P3}, height={shape.Height}");

        // Assert: seek and reverse continuation see every duplicate and neither neighbour.
        shape.LeafCounts.Sum().ShouldBe(5_002);
        shape.LeafCounts.Count.ShouldBe(1_666);
        shape.Fill.ShouldBeGreaterThanOrEqualTo(0.39);
        shape.Height.ShouldBeLessThanOrEqualTo(6);
        var reader = await harness.BeginAsync();
        var key = PaddedKey(50);
        foreach (bool reverse in new[] { false, true })
        {
            await using var cursor = index.OpenCursor(reader, new IndexKeyRange(key, key, true, true), reverse);
            int count = 0;
            while (await cursor.MoveNextAsync(CancellationToken.None))
            {
                cursor.CurrentEntryReference.ShouldBe((ulong)(reverse ? 4_999 - count : count));
                count++;
            }
            count.ShouldBe(5_000);
        }
        await harness.RollbackAsync(reader);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Indexing] - Split: a single-value leaf uses its upper fence through both insertion paths (#1196)")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Insert_SingleValueLeaf_ShouldUseRunEndOnlyWithGreaterFence(bool build, bool greaterFence)
    {
        // Arrange: split at the higher references, then erase the higher keys
        // remaining on the left. The fence survives erasure and the leaf is now
        // entirely one key. An equal-key fence cannot prove the run ends here.
        await using var harness = new IndexTestHarness();
        var setup = await harness.BeginAsync();
        var index = await harness.IndexManager.CreateIndexAsync(setup, 1, new IndexDefinition("ix_fence", IndexKind.BTree));
        for (ulong i = 0; i < 4; i++)
        {
            await index.InsertAsync(setup, PaddedKey(50), i, CancellationToken.None);
        }
        var upper = PaddedKey(greaterFence ? 51 : 50);
        for (ulong i = 9_000; i < 9_004; i++)
        {
            await index.InsertAsync(setup, upper, i, CancellationToken.None);
        }
        for (ulong i = 9_000; i < 9_002; i++)
        {
            await index.EraseAsync(harness.GetStorageTransaction(setup), upper, i, setup.Sequence, CancellationToken.None);
        }
        await harness.CommitAsync(setup);

        // Act
        var writer = await harness.BeginAsync();
        for (ulong i = 4; i < 5_000; i++)
        {
            if (build)
            {
                await index.InsertVersionAsync(harness.GetStorageTransaction(writer), PaddedKey(50), i, writer.Sequence, TransactionSequence.None, CancellationToken.None);
            }
            else
            {
                await index.InsertAsync(writer, PaddedKey(50), i, CancellationToken.None);
            }
        }
        await harness.CommitAsync(writer);
        var shape = BTreeShape.Read(harness.Storage, harness.IndexManager.ExportRegistrations().Single().RootPageId);
        _output.WriteLine($"single-value bounded run, greaterFence={greaterFence}: leaves={shape.LeafCounts.Count}, fill={shape.Fill:P3}, height={shape.Height}");

        // Assert
        shape.LeafCounts.Sum().ShouldBe(5_002);
        shape.LeafCounts.Count.ShouldBe(greaterFence ? 835 : 1_667);
        shape.Fill.ShouldBeGreaterThanOrEqualTo(greaterFence ? 0.78 : 0.39);
        var reader = await harness.BeginAsync();
        foreach (bool reverse in new[] { false, true })
        {
            await using var cursor = index.OpenCursor(reader, IndexKeyRange.All, reverse);
            int count = 0;
            while (await cursor.MoveNextAsync(CancellationToken.None))
            {
                int position = reverse ? 5_001 - count : count;
                cursor.CurrentKey.ShouldBe(position < 5_000 ? PaddedKey(50) : upper);
                cursor.CurrentEntryReference.ShouldBe((ulong)(position < 5_000 ? position : position + 4_002));
                count++;
            }
            count.ShouldBe(5_002);
        }
        await harness.RollbackAsync(reader);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - Split: an interior insertion retains the midpoint (#1196)")]
    public async Task Insert_InsideLeaf_ShouldKeepMidpointSplit()
    {
        // Arrange
        await using var harness = new IndexTestHarness();
        var writer = await harness.BeginAsync();
        var index = await harness.IndexManager.CreateIndexAsync(writer, 1, new IndexDefinition("ix_middle", IndexKind.BTree));
        for (int i = 0; i < 7; i++)
        {
            await index.InsertAsync(writer, PaddedKey(i * 2), (ulong)i, CancellationToken.None);
        }

        // Act
        await index.InsertAsync(writer, PaddedKey(5), 50, CancellationToken.None);
        await harness.CommitAsync(writer);

        // Assert
        var shape = BTreeShape.Read(harness.Storage, harness.IndexManager.ExportRegistrations().Single().RootPageId);
        shape.LeafCounts.ShouldBe(new[] { 4, 4 });
        shape.Height.ShouldBe(2);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Indexing] - Split: mixed leaves keep the midpoint and rightmost appends retain packing (#1196)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Insert_MixedLeafOrRightmostAppend_ShouldChooseEligibleSplit(bool higherKey)
    {
        // Arrange: seven maximum-size entries exactly fill a leaf.
        await using var harness = new IndexTestHarness();
        var writer = await harness.BeginAsync();
        var index = await harness.IndexManager.CreateIndexAsync(writer, 1, new IndexDefinition("ix_end", IndexKind.BTree));
        for (int i = 0; i < 7; i++)
        {
            await index.InsertAsync(writer, PaddedKey(higherKey && i == 6 ? 51 : 50), (ulong)i, CancellationToken.None);
        }

        // Act: the insertion slot is six with the higher key, seven otherwise.
        await index.InsertAsync(writer, PaddedKey(50), 7, CancellationToken.None);
        await harness.CommitAsync(writer);

        // Assert: a higher key makes this a mixed leaf, so use the midpoint.
        // A rightmost append still keeps an existing entry on each side.
        var shape = BTreeShape.Read(harness.Storage, harness.IndexManager.ExportRegistrations().Single().RootPageId);
        shape.LeafCounts.ShouldBe(higherKey ? new[] { 3, 5 } : new[] { 6, 2 });
        shape.Height.ShouldBe(2);
    }

    private static IndexKey PaddedKey(long value, int keyLength = BTreeIndex.MaxKeyLength)
    {
        var bytes = new byte[keyLength];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        bytes.AsSpan(sizeof(long)).Fill(0x5A);
        return new IndexKey(bytes);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - Split: a short higher key keeps a mixed leaf on the midpoint (#1196)")]
    public async Task Insert_RunBeforeShortKey_ShouldKeepMidpointSplit()
    {
        // Arrange: seven large duplicates and one small higher key fit together.
        // Moving only the small key will not free room for the next duplicate.
        await using var harness = new IndexTestHarness();
        var writer = await harness.BeginAsync();
        var index = await harness.IndexManager.CreateIndexAsync(writer, 1, new IndexDefinition("ix_sizes", IndexKind.BTree));
        var higher = new IndexKey(PaddedKey(51).Encoded[..sizeof(long)].ToArray());
        await index.InsertAsync(writer, higher, 99, CancellationToken.None);
        for (ulong i = 0; i < 7; i++)
        {
            await index.InsertAsync(writer, PaddedKey(50), i, CancellationToken.None);
        }

        // Act
        await index.InsertAsync(writer, PaddedKey(50), 7, CancellationToken.None);
        await harness.CommitAsync(writer);

        // Assert: the whole-page duplicate precondition excludes this leaf,
        // even though the higher key is short and the duplicate run dominates.
        var shape = BTreeShape.Read(harness.Storage, harness.IndexManager.ExportRegistrations().Single().RootPageId);
        shape.LeafCounts.ShouldBe(new[] { 4, 5 });
        var reader = await harness.BeginAsync();
        await using var cursor = index.OpenCursor(reader, IndexKeyRange.All);
        for (ulong i = 0; i < 9; i++)
        {
            (await cursor.MoveNextAsync(CancellationToken.None)).ShouldBeTrue();
            cursor.CurrentEntryReference.ShouldBe(i == 8 ? 99UL : i);
        }
        (await cursor.MoveNextAsync(CancellationToken.None)).ShouldBeFalse();
        await harness.RollbackAsync(reader);
    }
}
