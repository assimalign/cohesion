using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Indexing.Internal;
using Assimalign.Cohesion.Database.Indexing.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Indexing.Tests;

public sealed class BTreePayloadTests
{
    private readonly ITestOutputHelper _output;

    public BTreePayloadTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory(DisplayName = "Cohesion Test [Database.Indexing] - Payload: empty entries retain format-2 packing and allocate no payload storage (#1809)")]
    [InlineData(16, 183)]
    [InlineData(1024, 7)]
    public void InsertLeafEntry_EmptyPayload_ShouldKeepPackingAndAllocation(int keyLength, int expected)
    {
        // Arrange
        byte[] body = new byte[8192 - 96];
        byte[] key = new byte[keyLength];
        Measure(body, key); // warm up
        long allocation = long.MaxValue;

        // Act
        for (int round = 0; round < 3; round++) { allocation = Math.Min(allocation, Measure(body, key)); }
        var leaf = new BTreeNode(body);

        // Assert: format 2's independent layout calculation includes its directory slot.
        int oldCapacity = (body.Length - 32) / (2 + keyLength + 24 + 2);
        oldCapacity.ShouldBe(expected);
        leaf.EntryCount.ShouldBe((ushort)oldCapacity);
        allocation.ShouldBe(0);
        BTreeNode.LeafEntrySize(keyLength).ShouldBe(2 + keyLength + 24);
        _output.WriteLine($"{keyLength}-byte key: format 2 = {oldCapacity}, format 3 empty = {leaf.EntryCount}; allocation = {allocation} bytes.");

        static long Measure(byte[] body, byte[] key)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            var node = BTreeNode.Initialize(body, BTreeNode.LeafKind);
            while (node.FreeSpace >= BTreeNode.LeafEntrySize(key.Length))
            { node.InsertLeafEntry(node.EntryCount, key, node.EntryCount, 0, 0); }
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - Payload: three maximum entries fit and survive leaf and internal root growth (#1809)")]
    public async Task InsertAsync_MaximumKeyAndPayload_ShouldPreserveThroughRootGrowth()
    {
        // Arrange
        await using var harness = new IndexTestHarness();
        var setup = await harness.BeginAsync();
        var index = await harness.IndexManager.CreateIndexAsync(setup, 1, new IndexDefinition("payload"));
        byte[] payload = Payload(1);

        // Act / Assert
        for (int i = 0; i < 120; i++)
        {
            await index.InsertAsync(setup, Key(i), (ulong)i, payload);
            if (i == 2)
            {
                var shape = BTreeShape.Read(harness.Storage, index.RootPageId);
                shape.Height.ShouldBe(1);
                shape.LeafCounts.ShouldBe(new[] { 3 });
            }
            if (i == 3) { BTreeShape.Read(harness.Storage, index.RootPageId).Height.ShouldBe(2); }
        }
        await harness.CommitAsync(setup);
        BTreeShape.Read(harness.Storage, index.RootPageId).Height.ShouldBeGreaterThanOrEqualTo(3);
        await CheckAsync(index, 120, _ => payload);
        // Separators retain only the key and identity tiebreakers; no payload flag.
        VerifySeparators(harness.Storage, index.RootPageId);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - Payload: replacement growth splits, rollback restores, and copied cursor memory stays owned (#1809)")]
    public async Task ReplacePayloadAsync_GrowingPhysicalEntry_ShouldSplitAndRollback()
    {
        // Arrange: seven maximum keys fill the root without payloads.
        await using var harness = new IndexTestHarness();
        var setup = await harness.BeginAsync();
        var index = await harness.IndexManager.CreateIndexAsync(setup, 1, new IndexDefinition("payload"));
        await harness.CommitAsync(setup);
        using (var build = harness.Storage.BeginTransaction())
        {
            for (int i = 0; i < 7; i++) { await index.InsertVersionAsync(build, Key(i), (ulong)i, default, default); }
            build.Commit();
        }
        byte[] payload = Payload(7);

        // Act: a replacement exceeds the leaf's remaining space, forcing a root split.
        using (var replace = harness.Storage.BeginTransaction())
        {
            for (int i = 0; i < 7; i++) { (await index.ReplacePayloadAsync(replace, Key(i), (ulong)i, payload)).ShouldBeTrue(); }
            BTreeShape.Read(harness.Storage, index.RootPageId).Height.ShouldBe(2);
            await CheckAsync(index, 7, _ => payload);
            replace.Rollback();
        }

        // Assert
        BTreeShape.Read(harness.Storage, index.RootPageId).Height.ShouldBe(1);
        await CheckAsync(index, 7, _ => []);
        using (var replace = harness.Storage.BeginTransaction())
        {
            (await index.ReplacePayloadAsync(replace, Key(0), 0, payload)).ShouldBeTrue();
            (await index.ReplacePayloadAsync(replace, Key(99), 99, payload)).ShouldBeFalse();
            replace.Commit();
        }
        await using var cursor = index.OpenCursor(Snapshot(), IndexKeyRange.All);
        Should.Throw<InvalidOperationException>(() => cursor.CurrentPayload);
        (await cursor.MoveNextAsync()).ShouldBeTrue();
        var owned = cursor.CurrentPayload;
        using (var replace = harness.Storage.BeginTransaction())
        {
            await index.ReplacePayloadAsync(replace, Key(0), 0, ReadOnlyMemory<byte>.Empty);
            replace.Commit();
        }
        while (await cursor.MoveNextAsync()) { }
        await cursor.DisposeAsync();
        owned.ToArray().ShouldBe(payload);
        Should.Throw<ObjectDisposedException>(() => cursor.CurrentPayload);
        await CheckAsync(index, 7, _ => []);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - Payload: identity and uniqueness ignore payloads and stamped replacement is refused (#1809)")]
    public async Task InsertAsync_PayloadDiffers_ShouldKeepIdentityAndUniqueSemantics()
    {
        // Arrange
        await using var harness = new IndexTestHarness();
        var setup = await harness.BeginAsync();
        var index = await harness.IndexManager.CreateIndexAsync(setup, 1, new IndexDefinition("payload", IndexKind.BTree, true));
        await index.InsertAsync(setup, Key(0), 0, Payload(1));
        await harness.CommitAsync(setup);
        var writer = await harness.BeginAsync();

        // Act / Assert
        await Should.ThrowAsync<IndexUniqueViolationException>(async () => await index.InsertAsync(writer, Key(0), 1, Payload(2)));
        await harness.RollbackAsync(writer);
        using var bracket = harness.Storage.BeginTransaction();
        await Should.ThrowAsync<IndexException>(async () => await index.InsertVersionAsync(bracket, Key(0), 0, setup.Sequence, default, Payload(2)));
        var failure = await Should.ThrowAsync<IndexException>(async () => await index.ReplacePayloadAsync(bracket, Key(0), 0, Payload(2)));
        failure.Message.ShouldBe("Payload replacement requires a live physical entry with zero writer and deleter stamps.");
        await Should.ThrowAsync<IndexException>(async () => await index.InsertVersionAsync(bracket, Key(1), 1, default, default, new byte[1025]));
        await Should.ThrowAsync<IndexException>(async () => await index.ReplacePayloadAsync(bracket, Key(0), 0, new byte[1025]));
        await index.InsertVersionAsync(bracket, Key(1), 1, default, new(5), Payload(1));
        (await Should.ThrowAsync<IndexException>(async () => await index.ReplacePayloadAsync(bracket, Key(1), 1, Payload(2))))
            .Message.ShouldBe(failure.Message);
        bracket.Rollback();
        await CheckAsync(index, 1, _ => Payload(1));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - Payload: rollback, logical undo, purge, pruning and compaction preserve surviving bytes (#1809)")]
    public async Task Maintenance_PayloadEntries_ShouldPreserveSurvivors()
    {
        // Arrange
        await using var harness = new IndexTestHarness();
        var setup = await harness.BeginAsync();
        var index = await harness.IndexManager.CreateIndexAsync(setup, 1, new IndexDefinition("payload"));
        for (int i = 0; i < 30; i++) { await index.InsertAsync(setup, Key(i), (ulong)i, Payload(i)); }
        await harness.CommitAsync(setup);
        var aborted = await harness.BeginAsync();
        for (int i = 30; i < 60; i++) { await index.InsertAsync(aborted, Key(i), (ulong)i, Payload(i)); }
        await harness.RollbackAsync(aborted);
        await CheckAsync(index, 30, Payload);

        // Act: durable statement writes undone by identity, then a writer purge.
        var logical = await harness.BeginAsync();
        await index.DeleteAsync(logical, Key(0), 0);
        await index.InsertAsync(logical, Key(30), 30, Payload(30));
        await harness.LogicalRollbackAsync(logical, async bracket =>
        {
            await index.EraseAsync(bracket, Key(30), 30, logical.Sequence);
            await index.ClearDeleterAsync(bracket, Key(0), 0, logical.Sequence);
        });
        using (var build = harness.Storage.BeginTransaction())
        {
            await index.InsertVersionAsync(build, Key(30), 30, new(999), default, Payload(30));
            build.Commit();
        }
        using (var purge = harness.Storage.BeginTransaction())
        {
            index.PurgeWriters(purge, new HashSet<TransactionSequence> { new(999) }).ShouldBe(1);
            purge.Commit();
        }
        await CheckAsync(index, 30, Payload);
        var purgedWriter = await harness.BeginAsync();
        await index.DeleteAsync(purgedWriter, Key(5), 5);
        await index.InsertAsync(purgedWriter, Key(30), 30, Payload(30));
        await harness.LogicalRollbackAsync(purgedWriter, bracket =>
        {
            index.PurgeWriters(bracket, new HashSet<TransactionSequence> { purgedWriter.Sequence }).ShouldBe(2);
            return Task.CompletedTask;
        });
        await CheckAsync(index, 30, Payload);
        var deleting = await harness.BeginAsync();
        await index.DeleteAsync(deleting, Key(1), 1);
        await harness.CommitAsync(deleting);
        (await harness.IndexManager.PruneAsync(new(deleting.Sequence.Value + 1))).ShouldBe(1);
        // Reinsert the pruned key with a maximum payload, reclaiming its orphaned bytes.
        var insert = await harness.BeginAsync();
        await index.InsertAsync(insert, Key(1), 1, Payload(1));
        await harness.CommitAsync(insert);

        // Assert
        await CheckAsync(index, 30, Payload);
        BTreeShape.Read(harness.Storage, index.RootPageId).LeafCounts.Sum().ShouldBe(30);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - Payload: compaction accounts for orphaned payload bytes and retains every survivor (#1809)")]
    public async Task InsertVersionAsync_ErasedPayloadSpace_ShouldCompactWithoutSplitting()
    {
        // Arrange: three worst-case entries leave too little free space for another.
        await using var harness = new IndexTestHarness();
        var setup = await harness.BeginAsync();
        var index = await harness.IndexManager.CreateIndexAsync(setup, 1, new IndexDefinition("payload"));
        await harness.CommitAsync(setup);
        using (var build = harness.Storage.BeginTransaction())
        {
            for (int i = 0; i < 3; i++)
            { await index.InsertVersionAsync(build, Key(i), (ulong)i, default, default, Payload(i)); }
            build.Commit();
        }

        // Act: only reclaiming the removed entry's key AND payload avoids a split.
        using (var bracket = harness.Storage.BeginTransaction())
        {
            await index.EraseAsync(bracket, Key(1), 1, default);
            using (var page = harness.Storage.PageManager.GetPage((PageId)index.RootPageId))
            {
                var leaf = new BTreeNode(page.Page.AsBodySpan());
                leaf.FreeSpace.ShouldBeLessThan(BTreeNode.LeafEntrySize(1024, 1024));
                leaf.OrphanedLeafBytes.ShouldBe(2076);
            }
            await index.InsertVersionAsync(bracket, Key(1), 1, default, default, Payload(1));
            bracket.Commit();
        }

        // Assert
        BTreeShape.Read(harness.Storage, index.RootPageId).Height.ShouldBe(1);
        await CheckAsync(index, 3, Payload);
        using var handle = harness.Storage.PageManager.GetPage((PageId)index.RootPageId);
        new BTreeNode(handle.Page.AsBodySpan()).OrphanedLeafBytes.ShouldBe(0);
    }

    internal static IndexKey Key(int value)
    {
        var bytes = new byte[1024];
        bytes.AsSpan(0, 1016).Fill(0x2e);
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(1016), value);
        return new IndexKey(bytes);
    }

    internal static byte[] Payload(int value)
    {
        var bytes = new byte[1024];
        new Random(value).NextBytes(bytes);
        return bytes;
    }

    internal static TransactionSnapshot Snapshot() => new(new(10000), default, new(10000), []);

    internal static async Task CheckAsync(BTreeIndex index, int count, Func<int, byte[]> payload)
    {
        foreach (bool reverse in new[] { false, true })
        {
            await using var cursor = index.OpenCursor(Snapshot(), IndexKeyRange.All, reverse);
            int seen = 0;
            while (await cursor.MoveNextAsync())
            {
                seen.ShouldBeLessThan(count);
                int expected = reverse ? count - 1 - seen : seen;
                cursor.CurrentKey.ShouldBe(Key(expected));
                cursor.CurrentEntryReference.ShouldBe((ulong)expected);
                cursor.CurrentPayload.ToArray().ShouldBe(payload(expected));
                seen++;
            }
            seen.ShouldBe(count);
        }
    }

    private static void VerifySeparators(Storage.Storage storage, long pageId)
    {
        using var handle = storage.PageManager.GetPage((PageId)pageId);
        var node = new BTreeNode(handle.Page.AsBodySpan());
        if (node.IsLeaf) { return; }
        VerifySeparators(storage, node.LeftmostChild);
        int bytes = 0;
        for (int i = 0; i < node.EntryCount; i++)
        {
            node.GetKey(i).Length.ShouldBeLessThanOrEqualTo(BTreeIndex.MaxKeyLength);
            node.GetSeparatorTiebreaker(i).ShouldBe(BTreeTiebreaker.None);
            bytes += BTreeNode.InternalEntrySize(node.GetKey(i).Length, node.GetSeparatorTiebreaker(i));
            VerifySeparators(storage, node.GetChild(i));
        }
        (node.Body.Length - node.DataStart).ShouldBe(bytes);
    }
}
