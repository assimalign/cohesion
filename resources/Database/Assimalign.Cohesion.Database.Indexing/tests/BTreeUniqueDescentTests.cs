using System;
using System.Buffers.Binary;
using System.Linq;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Indexing.Internal;
using Assimalign.Cohesion.Database.Indexing.Tests.TestObjects;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Indexing.Tests;

[Collection(TimingCollection.Name)]
public sealed class BTreeUniqueDescentTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Indexing] - Unique insert: uniqueness and insertion share one descent, including a root split (#1239)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InsertAsync_UniqueKey_ShouldDescendOnce(bool split)
    {
        // Arrange
        await using var harness = new IndexTestHarness();
        var setup = await harness.BeginAsync();
        var index = await harness.IndexManager.CreateIndexAsync(setup, 1, new IndexDefinition("unique", IsUnique: true));
        await harness.CommitAsync(setup);
        long root = harness.IndexManager.ExportRegistrations()[0].RootPageId;
        var seed = await harness.BeginAsync();
        int value = 0;
        do
        {
            await index.InsertAsync(seed, IndexKey.FromInt64(value), (ulong)value + 1);
            value++;
        }
        while (split && HasRoom(harness, root));
        await harness.CommitAsync(seed);
        var writer = await harness.BeginAsync();
        int descents = 0;

        // Act
        try
        {
            BTreeIndex.DescentStarted = () => descents++;
            await index.InsertAsync(writer, IndexKey.FromInt64(value), (ulong)value + 1);
        }
        finally { BTreeIndex.DescentStarted = null; }
        await harness.CommitAsync(writer);

        // Assert
        descents.ShouldBe(1);
        var reader = await harness.BeginAsync();
        (await index.FindLiveEntryAsync(reader, IndexKey.FromInt64(value))).ShouldBe((ulong)value + 1);
        await harness.RollbackAsync(reader);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - Unique insert: cached ancestry survives recursive internal and root splits (#1239)")]
    public async Task InsertAsync_ShuffledWideUniqueKeys_ShouldDescendOncePerInsertAndKeepOrder()
    {
        // Arrange: wide keys force several internal levels and repeated recursive splits.
        await using var harness = new IndexTestHarness();
        var setup = await harness.BeginAsync();
        var index = await harness.IndexManager.CreateIndexAsync(setup, 1, new IndexDefinition("wide", IsUnique: true));
        await harness.CommitAsync(setup);
        var values = Enumerable.Range(0, 5_000).ToArray();
        new Random(1239).Shuffle(values);
        var writer = await harness.BeginAsync();
        int descents = 0;

        // Act
        try
        {
            BTreeIndex.DescentStarted = () => descents++;
            foreach (int value in values) { await index.InsertAsync(writer, WideKey(value), (ulong)value + 1); }
        }
        finally { BTreeIndex.DescentStarted = null; }
        await harness.CommitAsync(writer);

        // Assert
        descents.ShouldBe(values.Length);
        BTreeShape.Read(harness.Storage, harness.IndexManager.ExportRegistrations()[0].RootPageId).Height.ShouldBeGreaterThan(3);
        var reader = await harness.BeginAsync();
        await using var cursor = index.OpenCursor(reader, IndexKeyRange.All);
        int expected = 0;
        while (await cursor.MoveNextAsync())
        {
            cursor.CurrentKey.ShouldBe(WideKey(expected));
            cursor.CurrentEntryReference.ShouldBe((ulong)++expected);
        }
        expected.ShouldBe(values.Length);
        await harness.RollbackAsync(reader);
    }

    private static IndexKey WideKey(int value)
    {
        var bytes = new byte[BTreeIndex.MaxKeyLength];
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(bytes.Length - sizeof(int)), value);
        return new IndexKey(bytes);
    }

    private static bool HasRoom(IndexTestHarness harness, long root)
    {
        using var page = harness.Storage.PageManager.GetPage(root);
        var node = new BTreeNode(page.Page.AsBodySpan());
        return node.FreeSpace >= BTreeNode.LeafEntrySize(IndexKey.FromInt64(0).Length);
    }
}
