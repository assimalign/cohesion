using System;
using System.Buffers.Binary;
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

    private async Task<BTreeShape> MeasureAsync(string order, int seed)
    {
        // Arrange
        await using var harness = new IndexTestHarness();
        var setup = await harness.BeginAsync();
        var index = await harness.IndexManager.CreateIndexAsync(setup, 1, new IndexDefinition("ix_shape", IndexKind.BTree));
        await harness.CommitAsync(setup);
        var values = Enumerable.Range(0, 5_000).ToArray();
        if (order.StartsWith("random", StringComparison.Ordinal))
        {
            new Random(seed).Shuffle(values);
        }

        // Act
        var writer = await harness.BeginAsync();
        foreach (int value in values)
        {
            await index.InsertAsync(writer, PaddedKey(order.Contains("duplicates", StringComparison.Ordinal) ? 0 : value), (ulong)value, CancellationToken.None);
        }
        await harness.CommitAsync(writer);
        var shape = BTreeShape.Read(harness.Storage, harness.IndexManager.ExportRegistrations().Single().RootPageId);
        _output.WriteLine($"{order}, seed={seed}: leaves={shape.LeafCounts.Count}, entries={shape.LeafCounts.Sum()}, fill={shape.Fill:P3}, height={shape.Height}");

        // Assert
        shape.LeafCounts.Sum().ShouldBe(5_000);
        var reader = await harness.BeginAsync();
        await using var cursor = index.OpenCursor(reader, IndexKeyRange.All);
        int expected = 0;
        while (await cursor.MoveNextAsync(CancellationToken.None))
        {
            cursor.CurrentEntryReference.ShouldBe((ulong)expected++);
        }
        expected.ShouldBe(5_000);
        await harness.RollbackAsync(reader);
        return shape;
    }

    [Theory(DisplayName = "Cohesion Test [Database.Indexing] - Split: duplicate run ends before higher keys, through both insertion paths (#1196)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Insert_DuplicateRunBeforeHigherKeys_ShouldSplitAtRunEnd(bool build)
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
        shape.LeafCounts.Count.ShouldBeLessThanOrEqualTo(835);
        shape.Fill.ShouldBeGreaterThanOrEqualTo(0.78);
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

    [Theory(DisplayName = "Cohesion Test [Database.Indexing] - Split: run-end insertion uses its slot with two nonempty halves (#1196)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Insert_AtRunEnd_ShouldKeepBothSeparatorEntries(bool higherKey)
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

        // Assert: keep the existing entry on the right when appending, so the
        // separator never reads a nonexistent entry or points to an empty half.
        var shape = BTreeShape.Read(harness.Storage, harness.IndexManager.ExportRegistrations().Single().RootPageId);
        shape.LeafCounts.ShouldBe(higherKey ? new[] { 7, 1 } : new[] { 6, 2 });
        shape.Height.ShouldBe(2);
    }

    private static IndexKey PaddedKey(long value)
    {
        var bytes = new byte[BTreeIndex.MaxKeyLength];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        bytes.AsSpan(sizeof(long)).Fill(0x5A);
        return new IndexKey(bytes);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - Split: a small entry beyond a run leaves room for the incoming duplicate in one split (#1196)")]
    public async Task Insert_RunBeforeShortKey_ShouldLeaveRoomInOneSplit()
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

        // Assert: one split frees room and preserves both separator bounds.
        var shape = BTreeShape.Read(harness.Storage, harness.IndexManager.ExportRegistrations().Single().RootPageId);
        shape.LeafCounts.ShouldBe(new[] { 6, 3 });
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
