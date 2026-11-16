using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Indexing.Internal;
using Assimalign.Cohesion.Database.Indexing.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Indexing.Tests;

[Collection(TimingCollection.Name)]
public sealed class BTreePayloadRegressionTests
{
    private readonly ITestOutputHelper _output;

    public BTreePayloadRegressionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - Payload: identical replacement takes the page lock and preserves the body (#1809)")]
    public async Task ReplacePayloadAsync_IdenticalPayload_ShouldLockWithoutChangingBody()
    {
        // Arrange
        await using var harness = new IndexTestHarness();
        var index = await CreateAsync(harness);
        using (var build = harness.Storage.BeginTransaction())
        {
            await index.InsertVersionAsync(build, BTreePayloadTests.Key(0), 0, default, default, new byte[] { 1 });
            build.Commit();
        }
        harness.Storage.Checkpoint();
        byte[] before = Body(harness, index.RootPageId);
        using var first = harness.Storage.BeginTransaction();
        long journalBefore = harness.Storage.JournalLength;

        // Act: even the equal-byte path owns a first touch and conflicts with a rival.
        (await index.ReplacePayloadAsync(first, BTreePayloadTests.Key(0), 0, new byte[] { 1 })).ShouldBeTrue();
        Body(harness, index.RootPageId).ShouldBe(before);
        long imageBytes = harness.Storage.JournalLength - journalBefore;
        imageBytes.ShouldBeGreaterThan(0); // RegisterTouch journals an image after checkpoint.
        using (var rival = harness.Storage.BeginTransaction())
        {
            await Should.ThrowAsync<StorageTransactionException>(async () =>
                await index.ReplacePayloadAsync(rival, BTreePayloadTests.Key(0), 0, new byte[] { 1 }));
            rival.Rollback();
        }
        await index.ReplacePayloadAsync(first, BTreePayloadTests.Key(0), 0, new byte[] { 2 });
        using (var rival = harness.Storage.BeginTransaction())
        {
            await Should.ThrowAsync<StorageTransactionException>(async () =>
                await index.ReplacePayloadAsync(rival, BTreePayloadTests.Key(0), 0, new byte[] { 2 }));
            rival.Rollback();
        }
        first.Rollback();

        // Assert
        Body(harness, index.RootPageId).ShouldBe(before);
        await ExpectAsync(index, BTreePayloadTests.Key(0), BTreePayloadTests.Snapshot(), new byte[] { 1 });
        using (var unchanged = harness.Storage.BeginTransaction())
        {
            await index.ReplacePayloadAsync(unchanged, BTreePayloadTests.Key(0), 0, new byte[] { 1 });
            unchanged.Commit();
            using var journal = StorageJournal.Create(harness.Storage.Journal, leaveOpen: true);
            journal.ReadAll().Where(record => record.TransactionSequence == unchanged.Sequence)
                .ShouldNotContain(record => record.Type == JournalRecordType.PageDelta || record.Type == JournalRecordType.CommittedPageImage);
        }
        _output.WriteLine($"Identical first touch after checkpoint: {imageBytes} journal bytes; body unchanged.");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - Payload: equal-length replacement neither compacts nor moves entry bytes (#1809)")]
    public async Task ReplacePayloadAsync_EqualLength_ShouldOverwriteInPlace()
    {
        // Arrange
        await using var harness = new IndexTestHarness();
        var index = await CreateAsync(harness);
        using (var build = harness.Storage.BeginTransaction())
        {
            for (int i = 0; i < 3; i++)
            { await index.InsertVersionAsync(build, BTreePayloadTests.Key(i), (ulong)i, default, default, BTreePayloadTests.Payload(i)); }
            build.Commit();
        }
        byte[] before = Body(harness, index.RootPageId);
        int cleared = 0;
        using var replace = harness.Storage.BeginTransaction();

        // Act
        try
        {
            BTreeNode.PageCleared = _ => cleared++;
            for (int i = 0; i < 3; i++)
            { await index.ReplacePayloadAsync(replace, BTreePayloadTests.Key(i), (ulong)i, BTreePayloadTests.Payload(99)); }
        }
        finally { BTreeNode.PageCleared = null; }

        // Assert: the header/directory and every non-payload byte remain in place.
        cleared.ShouldBe(0);
        byte[] after = Body(harness, index.RootPageId);
        var oldNode = new BTreeNode(before);
        var newNode = new BTreeNode(after);
        oldNode.DataStart.ShouldBe(newNode.DataStart);
        newNode.OrphanedLeafBytes.ShouldBe(0);
        for (int i = 0; i < 3; i++)
        {
            int start = BinaryPrimitives.ReadUInt16LittleEndian(before.AsSpan(32 + 2 * i)) + 2 + 1024 + 24 + 2;
            before.AsSpan(start, 1024).Clear();
            after.AsSpan(start, 1024).Clear();
        }
        after.ShouldBe(before);
        replace.Rollback();
        await BTreePayloadTests.CheckAsync(index, 3, BTreePayloadTests.Payload);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Indexing] - Payload: each snapshot selects its version across delete and reference reuse (#1809)")]
    [InlineData(17, 1024)]
    [InlineData(0, 17)]
    [InlineData(17, 0)]
    public async Task OpenCursor_ReusedReference_ShouldSelectPayloadAtEverySnapshot(int firstLength, int secondLength)
    {
        // Arrange: keep real snapshot pins through insert, tombstone and re-insert.
        await using var harness = new IndexTestHarness();
        var index = await CreateAsync(harness);
        var key = BTreePayloadTests.Key(0);
        byte[] p1 = BTreePayloadTests.Payload(1)[..firstLength];
        byte[] p2 = BTreePayloadTests.Payload(2)[..secondLength];
        var before = await harness.Manager.BeginAsync();
        var insert = await harness.BeginAsync();
        await index.InsertAsync(insert, key, 7, p1);
        await harness.CommitAsync(insert);
        var inserted = await harness.Manager.BeginAsync();
        var delete = await harness.BeginAsync();
        await index.DeleteAsync(delete, key, 7);
        await harness.CommitAsync(delete);
        var tombstone = await harness.Manager.BeginAsync();
        await ExpectAsync(index, key, tombstone.Snapshot, null);

        // Act
        var reinsert = await harness.BeginAsync();
        await index.InsertAsync(reinsert, key, 7, p2);
        await harness.CommitAsync(reinsert);
        var reinserted = await harness.Manager.BeginAsync();

        // Assert: the older version still has its own bytes, and deletion is a gap.
        await ExpectAsync(index, key, before.Snapshot, null);
        await ExpectAsync(index, key, inserted.Snapshot, p1);
        await ExpectAsync(index, key, tombstone.Snapshot, null);
        await ExpectAsync(index, key, reinserted.Snapshot, p2);
        foreach (var reader in new[] { before, inserted, tombstone, reinserted })
        { await harness.Manager.CommitAsync(reader); }
    }

    [Theory(DisplayName = "Cohesion Test [Database.Indexing] - Payload: replacement refuses the newest stamped version even beside writer zero (#1809)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacePayloadAsync_PhysicalAndStampedVersions_ShouldRefuseNewest(bool reverseInsertion)
    {
        // Arrange: unsigned writer order makes writer zero the oldest in either insertion order.
        await using var harness = new IndexTestHarness();
        var index = await CreateAsync(harness);
        var key = BTreePayloadTests.Key(0);
        using (var build = harness.Storage.BeginTransaction())
        {
            foreach (ulong writer in reverseInsertion ? new ulong[] { 5, 0 } : new ulong[] { 0, 5 })
            { await index.InsertVersionAsync(build, key, 7, new(writer), default, BTreePayloadTests.Payload((int)writer)); }
            build.Commit();
        }
        byte[] before = Body(harness, index.RootPageId);
        using var replace = harness.Storage.BeginTransaction();
        long journal = harness.Storage.JournalLength;

        // Act / Assert: refusal is before any page touch.
        await Should.ThrowAsync<IndexException>(async () =>
            await index.ReplacePayloadAsync(replace, key, 7, BTreePayloadTests.Payload(9)));
        harness.Storage.JournalLength.ShouldBe(journal);
        Body(harness, index.RootPageId).ShouldBe(before);
        replace.Rollback();
    }

    [Theory(DisplayName = "Cohesion Test [Database.Indexing] - Payload: mixed entry sizes require one leaf split (#1809)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SplitLeaf_MixedSizes_ShouldFitIncomingEntryInOneSplit(bool replacement)
    {
        // Arrange: the insert shape is exactly 3 * 2078 + 41 * 44 = 8038 bytes.
        // Replacement also needs its old 1052-byte identity: 3 * 2078 + 17 * 44 + 1052 = 8034.
        await using var harness = new IndexTestHarness();
        var index = await CreateAsync(harness);
        using (var build = harness.Storage.BeginTransaction())
        {
            if (replacement) { await index.InsertVersionAsync(build, MixedKey(0, true), 0, default, default); }
            for (int i = 1; i <= 3; i++)
            { await index.InsertVersionAsync(build, MixedKey(i, true), (ulong)i, default, default, BTreePayloadTests.Payload(i)); }
            for (int i = 0; i < (replacement ? 17 : 41); i++)
            { await index.InsertVersionAsync(build, MixedKey(i, false), (ulong)(100 + i), default, default); }
            build.Commit();
        }
        BTreeShape.Read(harness.Storage, index.RootPageId).Height.ShouldBe(1);

        // Act
        using (var change = harness.Storage.BeginTransaction())
        {
            if (replacement) { (await index.ReplacePayloadAsync(change, MixedKey(0, true), 0, BTreePayloadTests.Payload(0))).ShouldBeTrue(); }
            else { await index.InsertVersionAsync(change, MixedKey(0, true), 0, default, default, BTreePayloadTests.Payload(0)); }
            change.Commit();
        }

        // Assert: 2 leaves and 1 root, with 1 separator; no shape pin is moved.
        var shape = BTreeShape.Read(harness.Storage, index.RootPageId);
        using (var page = harness.Storage.PageManager.GetPage(index.RootPageId))
        {
            _output.WriteLine($"{(replacement ? "Replacement" : "Insert")}: leaves {string.Join(",", shape.LeafCounts)}; " +
                $"{shape.LeafCounts.Count + 1} pages; {new BTreeNode(page.Page.AsBodySpan()).EntryCount} separators.");
        }
        shape.Height.ShouldBe(2);
        shape.LeafCounts.ShouldBe(replacement ? new[] { 3, 18 } : new[] { 3, 42 });
        using var root = harness.Storage.PageManager.GetPage(index.RootPageId);
        new BTreeNode(root.Page.AsBodySpan()).EntryCount.ShouldBe((ushort)1);
        await using var cursor = index.OpenCursor(BTreePayloadTests.Snapshot(), IndexKeyRange.All);
        int seen = 0;
        while (await cursor.MoveNextAsync())
        {
            int reference = (int)cursor.CurrentEntryReference;
            reference.ShouldBe(seen < 4 ? seen : 100 + seen - 4);
            cursor.CurrentKey.ShouldBe(MixedKey(seen < 4 ? seen : seen - 4, seen < 4));
            cursor.CurrentPayload.ToArray().ShouldBe(reference < 4 ? BTreePayloadTests.Payload(reference) : Array.Empty<byte>());
            seen++;
        }
        seen.ShouldBe(replacement ? 21 : 45);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Indexing] - Payload: every entry-access cancellation preserves the old mapping before rollback (#1809)")]
    [InlineData(0)] // Growth through a split.
    [InlineData(1)] // Different length requiring compaction.
    [InlineData(2)] // Equal-length overwrite.
    public async Task ReplacePayloadAsync_CanceledAtEveryAccess_ShouldKeepOldPayload(int scenario)
    {
        // Arrange / Act: a dry run records every reachable entry-access cancellation point.
        int points = await RunAsync(0);
        // Equal-length overwrite has only eight accesses, with no split or compaction.
        if (scenario == 2) { points.ShouldBePositive(); }
        else { points.ShouldBeGreaterThan(10); }
        for (int point = 1; point <= points; point++) { await RunAsync(point); }
        _output.WriteLine($"Replacement scenario {scenario}: {points} reachable entry-access cancellation points.");

        async Task<int> RunAsync(int cancelAt)
        {
            await using var harness = new IndexTestHarness();
            var index = await SeedPhysicalAsync(harness, scenario != 0);
            int count = scenario == 0 ? 7 : 3;
            using var change = harness.Storage.BeginTransaction();
            using var cancellation = new CancellationTokenSource();
            int accessed = 0;
            OperationCanceledException? failure = null;
            try
            {
                BTreeIndex.EntryAccessed = () => { if (++accessed == cancelAt) { cancellation.Cancel(); } };
                try { await index.ReplacePayloadAsync(change, BTreePayloadTests.Key(0), 0,
                    BTreePayloadTests.Payload(99).AsMemory(0, scenario == 1 ? 1023 : 1024), cancellation.Token); }
                catch (OperationCanceledException exception) { failure = exception; }
            }
            finally { BTreeIndex.EntryAccessed = null; }

            // Assert: read while the failed bracket is still open, then validate rollback.
            if (cancelAt > 0)
            {
                failure.ShouldNotBeNull().CancellationToken.ShouldBe(cancellation.Token);
                await BTreePayloadTests.CheckAsync(index, count, i => scenario == 0 ? [] : BTreePayloadTests.Payload(i));
            }
            else { failure.ShouldBeNull(); }
            change.Rollback();
            BTreeShape.Read(harness.Storage, index.RootPageId).LeafCounts.ShouldBe(new[] { count });
            await BTreePayloadTests.CheckAsync(index, count, i => scenario == 0 ? [] : BTreePayloadTests.Payload(i));
            return accessed;
        }
    }

    [Theory(DisplayName = "Cohesion Test [Database.Indexing] - Payload: a split or compaction initialization fault keeps the old mapping (#1809)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacePayloadAsync_InitializationFault_ShouldKeepOldPayloadBeforeAndAfterRollback(bool compaction)
    {
        // Arrange
        await using var harness = new IndexTestHarness();
        var index = await SeedPhysicalAsync(harness, compaction);
        int count = compaction ? 3 : 7;
        using var change = harness.Storage.BeginTransaction();
        int injected = 0;

        // Act: fail in sibling initialization or staged compaction, before the source changes.
        try
        {
            BTreeNode.PageCleared = kind => { kind.ShouldBe(BTreeNode.LeafKind); injected++; throw new IOException(); };
            await Should.ThrowAsync<IOException>(async () =>
                await index.ReplacePayloadAsync(change, BTreePayloadTests.Key(0), 0,
                    BTreePayloadTests.Payload(99).AsMemory(0, compaction ? 1023 : 1024)));
        }
        finally { BTreeNode.PageCleared = null; }

        // Assert
        injected.ShouldBe(1);
        await BTreePayloadTests.CheckAsync(index, count, i => compaction ? BTreePayloadTests.Payload(i) : []);
        change.Rollback();
        BTreeShape.Read(harness.Storage, index.RootPageId).LeafCounts.ShouldBe(new[] { count });
        await BTreePayloadTests.CheckAsync(index, count, i => compaction ? BTreePayloadTests.Payload(i) : []);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Indexing] - Payload: unique insert prunes payload tombstones before compaction or splitting (#1809)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InsertAsync_PruneBoundWithPayload_ShouldPreserveSurvivors(bool split)
    {
        // Arrange: one obsolete version, optionally another not yet below the horizon.
        await using var harness = new IndexTestHarness(pruneBound: () => new(10));
        var setup = await harness.BeginAsync();
        var index = await harness.IndexManager.CreateIndexAsync(setup, 1, new IndexDefinition("payload", IndexKind.BTree, true));
        await harness.CommitAsync(setup);
        using (var build = harness.Storage.BeginTransaction())
        {
            await index.InsertVersionAsync(build, BTreePayloadTests.Key(0), 0, new(1), new(5),
                split ? new byte[] { 1 } : BTreePayloadTests.Payload(0));
            if (split) { await index.InsertVersionAsync(build, BTreePayloadTests.Key(0), 1, new(6), new(11), BTreePayloadTests.Payload(1)); }
            for (int i = 2; i < 4; i++)
            { await index.InsertVersionAsync(build, BTreePayloadTests.Key(i), (ulong)i, default, default, BTreePayloadTests.Payload(i)); }
            build.Commit();
        }

        // Act
        var insert = await harness.BeginAsync();
        await index.InsertAsync(insert, BTreePayloadTests.Key(0), 7, BTreePayloadTests.Payload(7));
        await harness.CommitAsync(insert);

        // Assert
        var shape = BTreeShape.Read(harness.Storage, index.RootPageId);
        shape.Height.ShouldBe(split ? 2 : 1);
        shape.LeafCounts.Sum().ShouldBe(split ? 4 : 3);
        await ExpectAsync(index, BTreePayloadTests.Key(0), BTreePayloadTests.Snapshot(), BTreePayloadTests.Payload(7));
        for (int i = 2; i < 4; i++)
        { await ExpectAsync(index, BTreePayloadTests.Key(i), BTreePayloadTests.Snapshot(), BTreePayloadTests.Payload(i)); }
    }

    [Theory(DisplayName = "Cohesion Test [Database.Indexing] - Payload: failed parent insertion exposes the pre-existing bounded-seek window (#1809)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SplitLeaf_ParentWriteConflict_ShouldReproduceSeekWindowUntilRollback(bool replacement)
    {
        // Arrange: two leaves; the right leaf contains seven small-payload entries.
        await using var harness = new IndexTestHarness();
        var index = await CreateAsync(harness);
        using (var build = harness.Storage.BeginTransaction())
        {
            for (int i = 0; i < 13; i++)
            { await index.InsertVersionAsync(build, BTreePayloadTests.Key(i), (ulong)i,
                replacement ? default : new TransactionSequence(1), default, new byte[] { 1 }); }
            build.Commit();
        }
        using var blocker = harness.Storage.BeginTransaction();
        using (var parent = harness.Storage.OpenPageForWrite(blocker, index.RootPageId)) { }
        using var change = harness.Storage.BeginTransaction();

        // Act: the leaf splits, but the parent separator cannot be installed.
        await Should.ThrowAsync<StorageTransactionException>(async () =>
        {
            if (replacement) { await index.ReplacePayloadAsync(change, BTreePayloadTests.Key(10), 10, BTreePayloadTests.Payload(99)); }
            else { await index.InsertVersionAsync(change, BTreePayloadTests.Key(10), 100, default, default, BTreePayloadTests.Payload(99)); }
        });

        // Assert: full chain walk sees the old payload; a bounded seek skips its sibling.
        await using (var scan = index.OpenCursor(BTreePayloadTests.Snapshot(), IndexKeyRange.All))
        {
            int seen = 0;
            while (await scan.MoveNextAsync())
            {
                scan.CurrentEntryReference.ShouldBe((ulong)seen++);
                scan.CurrentPayload.ToArray().ShouldBe(new byte[] { 1 });
            }
            seen.ShouldBe(13);
        }
        await ExpectAsync(index, BTreePayloadTests.Key(10), BTreePayloadTests.Snapshot(), null);
        await using (var reverse = index.OpenCursor(BTreePayloadTests.Snapshot(), IndexKeyRange.All, reverse: true))
        {
            int seen = 0;
            while (await reverse.MoveNextAsync()) { reverse.CurrentEntryReference.ShouldBe((ulong)(8 - seen++)); }
            seen.ShouldBe(9); // The missing separator also misroutes the initial rightmost descent.
        }
        change.Rollback();
        blocker.Rollback();
        BTreeShape.Read(harness.Storage, index.RootPageId).LeafCounts.ShouldBe(new[] { 6, 7 });
        await ExpectAsync(index, BTreePayloadTests.Key(10), BTreePayloadTests.Snapshot(), new byte[] { 1 });
        _output.WriteLine("Parent conflict: forward chain sees 13 old entries; reverse scan sees 9; bounded seek sees 0 instead of 1 until rollback.");
    }

    [Theory(DisplayName = "Cohesion Test [Database.Indexing] - Payload: a midpoint insertion follows the truncated separator when testing byte fit (#1809)")]
    [InlineData(15, 3, 6)]
    [InlineData(257, 4, 5)]
    public async Task SplitLeaf_IncomingAtMidpoint_ShouldTestActualSeparatorSide(int value, int left, int right)
    {
        // Arrange: 3 large entries then 5 small ones. The midpoint's left half
        // cannot admit a maximum entry; its right half can. Both incoming keys
        // sort at slot 4, but the separator (truncated before 300's final byte)
        // routes 15 left and 257 right.
        await using var harness = new IndexTestHarness();
        var index = await CreateAsync(harness);
        using var change = harness.Storage.BeginTransaction();
        for (int i = 1; i <= 3; i++)
        { await index.InsertVersionAsync(change, MixedKey(i, true), (ulong)i, default, default, BTreePayloadTests.Payload(i)); }
        foreach (int small in new[] { 10, 300, 301, 302, 303 })
        { await index.InsertVersionAsync(change, MixedKey(small, false), (ulong)small, default, default); }
        byte[] bytes = new byte[1024];
        MixedKey(value, false).Encoded.Span.CopyTo(bytes);

        // Act
        await index.InsertVersionAsync(change, new IndexKey(bytes), 999, default, default, BTreePayloadTests.Payload(999));
        change.Commit();

        // Assert
        BTreeShape.Read(harness.Storage, index.RootPageId).LeafCounts.ShouldBe(new[] { left, right });
    }

    private static async Task<BTreeIndex> CreateAsync(IndexTestHarness harness)
    {
        var setup = await harness.BeginAsync();
        var index = await harness.IndexManager.CreateIndexAsync(setup, 1, new IndexDefinition("payload"));
        await harness.CommitAsync(setup);
        return index;
    }

    private static async Task<BTreeIndex> SeedPhysicalAsync(IndexTestHarness harness, bool maximumPayload)
    {
        var index = await CreateAsync(harness);
        using var build = harness.Storage.BeginTransaction();
        for (int i = 0; i < (maximumPayload ? 3 : 7); i++)
        { await index.InsertVersionAsync(build, BTreePayloadTests.Key(i), (ulong)i, default, default,
            maximumPayload ? BTreePayloadTests.Payload(i) : ReadOnlyMemory<byte>.Empty); }
        build.Commit();
        return index;
    }

    private static byte[] Body(IndexTestHarness harness, long id)
    {
        using var page = harness.Storage.PageManager.GetPage(id);
        return page.Page.AsBodySpan().ToArray();
    }

    private static IndexKey MixedKey(int value, bool large)
    {
        var bytes = new byte[large ? 1024 : 16];
        bytes[0] = large ? (byte)0x10 : (byte)0x20;
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(bytes.Length - 4), value);
        return new IndexKey(bytes);
    }

    private static async Task ExpectAsync(BTreeIndex index, IndexKey key, TransactionSnapshot snapshot, byte[]? payload)
    {
        foreach (bool reverse in new[] { false, true })
        {
            await using var cursor = index.OpenCursor(snapshot, new IndexKeyRange(key, key, true, true), reverse);
            (await cursor.MoveNextAsync(CancellationToken.None)).ShouldBe(payload is not null);
            if (payload is not null)
            {
                cursor.CurrentPayload.ToArray().ShouldBe(payload);
                (await cursor.MoveNextAsync(CancellationToken.None)).ShouldBeFalse();
            }
        }
    }
}
