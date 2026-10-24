using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Indexing.Tests.TestObjects;
using Assimalign.Cohesion.Database.Indexing.Internal;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Indexing.Tests;

/// <summary>Continuation, cancellation, ownership and concurrency regressions for incremental cursors.</summary>
[Collection(BTreeRollbackRaceCollection.Name)]
public sealed class BTreeCursorTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - Cursor: failed apply and manager replacement retain the physical tree latch (#1542)")]
    public async Task MoveNextAsync_ManagerReplacedAfterFailedApply_ShouldExcludePausedRewrite()
    {
        // Arrange: the same failed-bracket/reattach sequence used by Graph and Documents.
        await using var harness = new IndexTestHarness();
        var index = await CreateAsync(harness);
        var seed = await harness.BeginAsync();
        for (int i = 0; i < 100; i++) { await index.InsertAsync(seed, IndexKey.FromInt64(i), (ulong)i); }
        await harness.CommitAsync(seed);
        var reader = await harness.Manager.BeginAsync();
        await using var cursor = index.OpenCursor(reader.Snapshot, IndexKeyRange.All);
        cursor.BatchSizeOverride = 64;
        for (ulong i = 0; i < 64; i++)
        {
            (await cursor.MoveNextAsync()).ShouldBeTrue();
            cursor.CurrentEntryReference.ShouldBe(i);
        }
        var failed = await harness.BeginAsync();
        try
        {
            await index.InsertAsync(failed, IndexKey.FromInt64(100), 100);
            throw new InvalidOperationException("forced apply failure");
        }
        catch (InvalidOperationException) { await harness.RollbackAsync(failed); }
        var replacement = BTreeIndexManager.Create(new BTreeIndexManagerOptions
        {
            Storage = harness.Storage,
            TransactionSource = harness.GetStorageTransaction,
            LockManager = harness.LockManager,
            ExistingIndexes = harness.IndexManager.ExportRegistrations(),
        });
        replacement.TryGetIndex(1, index.Name, out var attached).ShouldBeTrue();
        using var cleared = new ManualResetEventSlim();
        using var resumeWriter = new ManualResetEventSlim();
        using var readerStarted = new ManualResetEventSlim();
        BTreeNode.PageCleared = kind =>
        {
            if (kind != BTreeNode.InternalKind) { return; }
            cleared.Set();
            resumeWriter.Wait(TimeSpan.FromSeconds(30)).ShouldBeTrue();
        };
        var writer = await harness.BeginAsync();
        var rewrite = DedicatedTestTask.Run(async () =>
        {
            for (int i = 100; i < 400; i++) { await attached.InsertAsync(writer, IndexKey.FromInt64(i), (ulong)i); }
            await harness.CommitAsync(writer);
        });
        Task<bool>? move = null;
        try
        {
            cleared.Wait(TimeSpan.FromSeconds(30)).ShouldBeTrue();
            // Act: the root is cleared but has not been stamped as a node yet.
            move = DedicatedTestTask.Run(async () => { readerStarted.Set(); return await cursor.MoveNextAsync(); });
            readerStarted.Wait(TimeSpan.FromSeconds(30)).ShouldBeTrue();
            if (!ReferenceEquals(index.Latch, attached.Latch))
            {
                // On the defective implementation this deterministically reads the cleared root.
                // No scheduling window or timeout is the evidence for the failure.
                await move;
            }
            index.Latch.ShouldBeSameAs(attached.Latch);
        }
        finally
        {
            resumeWriter.Set();
            try { await rewrite; }
            finally { BTreeNode.PageCleared = null; }
        }

        // Assert: the surviving cursor continues its pinned view after the replacement writes.
        (await move!).ShouldBeTrue();
        cursor.CurrentEntryReference.ShouldBe(64UL);
        var remaining = new List<ulong> { cursor.CurrentEntryReference };
        while (await cursor.MoveNextAsync()) { remaining.Add(cursor.CurrentEntryReference); }
        remaining.ShouldBe(Enumerable.Range(64, 36).Select(i => (ulong)i));
        await harness.Manager.CommitAsync(reader);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - Cursor: borrowed bounds are captured when the cursor opens (#1542)")]
    public async Task OpenCursor_MutatedBoundMemory_ShouldKeepCapturedRange()
    {
        // Arrange
        await using var harness = new IndexTestHarness();
        var index = await CreateAsync(harness);
        var writer = await harness.BeginAsync();
        for (int i = 0; i < 200; i++) { await index.InsertAsync(writer, IndexKey.FromInt64(i), (ulong)i); }
        await harness.CommitAsync(writer);
        var reader = await harness.Manager.BeginAsync();
        byte[] start = IndexKey.FromInt64(50).Encoded.ToArray();
        byte[] end = IndexKey.FromInt64(150).Encoded.ToArray();
        await using var cursor = index.OpenCursor(reader.Snapshot, new IndexKeyRange(new IndexKey(start), new IndexKey(end)));

        // Act
        Array.Fill(start, byte.MaxValue);
        Array.Clear(end);
        var found = new List<ulong>();
        while (await cursor.MoveNextAsync()) { found.Add(cursor.CurrentEntryReference); }

        // Assert
        found.ShouldBe(Enumerable.Range(50, 100).Select(i => (ulong)i));
        await harness.Manager.CommitAsync(reader);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Indexing] - Cursor: empty leaves yield bounded latch work using logical fences (#1542)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MoveNextAsync_EmptyLeafChain_ShouldYieldBetweenPages(bool reverse)
    {
        // Arrange: erase all but the far endpoint, retaining the split tree's empty leaves.
        await using var harness = new IndexTestHarness();
        var index = await CreateAsync(harness);
        var writer = await harness.BeginAsync();
        for (int i = 0; i < 2_000; i++) { await index.InsertAsync(writer, WideKey(i), (ulong)i); }
        await harness.CommitAsync(writer);
        using (var erase = harness.Storage.BeginTransaction())
        {
            for (int i = reverse ? 1 : 0; i < (reverse ? 2_000 : 1_999); i++)
            {
                await index.EraseAsync(erase, WideKey(i), (ulong)i, writer.Sequence);
            }
            erase.Commit();
        }
        var reader = await harness.Manager.BeginAsync();
        await using var cursor = index.OpenCursor(reader.Snapshot, IndexKeyRange.All, reverse);
        long previous = 0;
        int batches = 0;
        cursor.BatchRead = () =>
        {
            (cursor.PagesVisited - previous).ShouldBeLessThanOrEqualTo(8);
            previous = cursor.PagesVisited;
            batches++;
            // A writer can acquire immediately between each empty-leaf batch.
            index.Latch.EnterWrite();
            index.Latch.ExitWrite();
        };

        // Act / Assert
        (await cursor.MoveNextAsync()).ShouldBeTrue();
        cursor.CurrentEntryReference.ShouldBe(reverse ? 0UL : 1_999UL);
        batches.ShouldBeGreaterThan(100);
        (await cursor.MoveNextAsync()).ShouldBeFalse();
        await harness.Manager.CommitAsync(reader);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Indexing] - Cursor: concurrent committed splits and purged writers preserve a pinned snapshot (#1542)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MoveNextAsync_RacingSplitsAndPurge_ShouldExerciseOverlapWithoutDuplicates(bool reverse)
    {
        // Arrange: one old reader spans every new commit and aborted-writer purge.
        await using var harness = new IndexTestHarness();
        var index = await CreateAsync(harness);
        var seed = await harness.BeginAsync();
        for (int i = 0; i < 1_000; i++) { await index.InsertAsync(seed, WideKey(i), (ulong)i); }
        await harness.CommitAsync(seed);
        var reader = await harness.Manager.BeginAsync();
        var snapshot = reader.Snapshot;
        var expected = Enumerable.Range(0, 1_000).Select(i => (ulong)i);
        if (reverse) { expected = expected.Reverse(); }
        ulong[] committed = expected.ToArray();
        using var stop = new CancellationTokenSource();
        Exception? failure = null;
        long writes = 0;
        long purges = 0;
        long scans = 0;
        long overlaps = 0;
        var writer = DedicatedTestTask.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var transaction = await harness.BeginAsync();
                    ulong offset = 10_000UL + (ulong)Interlocked.Read(ref writes) * 100;
                    for (int i = 0; i < 100; i++)
                    {
                        await index.InsertAsync(transaction, WideKey(i * 10), offset + (ulong)i);
                    }
                    if ((offset / 100) % 2 == 0)
                    {
                        await harness.CommitAsync(transaction);
                    }
                    else
                    {
                        await harness.LogicalRollbackAsync(transaction, bracket =>
                        {
                            long removed = index.PurgeWriters(bracket, new HashSet<TransactionSequence> { transaction.Sequence });
                            removed.ShouldBe(100);
                            return Task.CompletedTask;
                        });
                        Interlocked.Increment(ref purges);
                    }
                    Interlocked.Increment(ref writes);
                }
            }
            catch (Exception exception) { Interlocked.CompareExchange(ref failure, exception, null); }
        });
        var scanner = DedicatedTestTask.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    long before = Interlocked.Read(ref writes);
                    (await ReadAsync(index, snapshot, IndexKeyRange.All, reverse)).ShouldBe(committed);
                    if (Interlocked.Read(ref writes) > before) { Interlocked.Increment(ref overlaps); }
                    Interlocked.Increment(ref scans);
                }
            }
            catch (Exception exception) { Interlocked.CompareExchange(ref failure, exception, null); }
        });

        // Act: run for a minimum, then until counters prove all races ran, with a ceiling.
        var elapsed = Stopwatch.StartNew();
        try
        {
            while (Volatile.Read(ref failure) is null &&
                (elapsed.Elapsed < TimeSpan.FromSeconds(2) ||
                    ((Interlocked.Read(ref writes) < 10 || Interlocked.Read(ref purges) < 5 ||
                      Interlocked.Read(ref scans) < 20 || Interlocked.Read(ref overlaps) < 5) && elapsed.Elapsed < TimeSpan.FromSeconds(60))))
            {
                await Task.Delay(20, CancellationToken.None);
            }
        }
        finally { stop.Cancel(); await Task.WhenAll(writer, scanner); }

        // Assert
        failure.ShouldBeNull();
        writes.ShouldBeGreaterThanOrEqualTo(10);
        purges.ShouldBeGreaterThanOrEqualTo(5);
        scans.ShouldBeGreaterThanOrEqualTo(20);
        overlaps.ShouldBeGreaterThanOrEqualTo(5);
        await harness.Manager.CommitAsync(reader);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Indexing] - Cursor: bounds and duplicate runs remain exact in either direction (#1542)")]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task MoveNextAsync_BoundsAcrossBatches_ShouldReturnEveryDuplicateOnce(bool reverse, bool includeStart, bool includeEnd)
    {
        // Arrange: wide equal keys force duplicate runs and internal splits.
        await using var harness = new IndexTestHarness();
        var index = await CreateAsync(harness);
        var writer = await harness.BeginAsync();
        for (int i = 0; i < 1_200; i++)
        {
            await index.InsertAsync(writer, WideKey(i / 300), (ulong)i, CancellationToken.None);
        }
        await harness.CommitAsync(writer);
        var reader = await harness.Manager.BeginAsync();
        var ranges = new[]
        {
            new IndexKeyRange(WideKey(1), WideKey(2), includeStart, includeEnd),
            new IndexKeyRange(null, WideKey(1), true, includeEnd),
            new IndexKeyRange(WideKey(2), null, includeStart, false),
            new IndexKeyRange(WideKey(1), WideKey(1), includeStart, includeEnd),
            new IndexKeyRange(WideKey(3), WideKey(0), includeStart, includeEnd),
            IndexKeyRange.All,
        };

        // Act / Assert
        foreach (var range in ranges)
        {
            var expected = Enumerable.Range(0, 1_200).Where(i =>
                (range.Start is not { } start || WideKey(i / 300).CompareTo(start) > 0 ||
                    (includeStart && WideKey(i / 300).Equals(start))) &&
                (range.End is not { } end || WideKey(i / 300).CompareTo(end) < 0 ||
                    (includeEnd && WideKey(i / 300).Equals(end)))).Select(i => (ulong)i);
            if (reverse) { expected = expected.Reverse(); }
            (await ReadAsync(index, reader.Snapshot, range, reverse)).ShouldBe(expected);
        }
        await harness.Manager.CommitAsync(reader);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Indexing] - Cursor: full writer identity continues across physical removal (#1542)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MoveNextAsync_ReusedReferenceAndErasedContinuation_ShouldRetainWriterOrder(bool reverse)
    {
        // Arrange: 600 versions of one reference, every version visible to this artificial snapshot.
        await using var harness = new IndexTestHarness();
        var index = await CreateAsync(harness);
        var key = WideKey(0);
        using (var build = harness.Storage.BeginTransaction())
        {
            for (ulong i = 1; i <= 600; i++)
            {
                await index.InsertVersionAsync(build, key, 7, new TransactionSequence(i), TransactionSequence.None);
            }
            build.Commit();
        }
        var snapshot = new TransactionSnapshot(new TransactionSequence(1_000), TransactionSequence.None,
            new TransactionSequence(1_000), []);
        await using var cursor = index.OpenCursor(snapshot, IndexKeyRange.All, reverse);
        cursor.BatchSizeOverride = 64;

        // Act: remove the batch's last identity, so continuation cannot depend on finding its slot.
        for (int i = 0; i < BTreeCursor.BatchSize; i++) { (await cursor.MoveNextAsync()).ShouldBeTrue(); }
        ulong lastWriter = reverse ? 537UL : 64UL;
        cursor.Continuation!.Value.Writer.ShouldBe(lastWriter);
        using (var erase = harness.Storage.BeginTransaction())
        {
            await index.EraseAsync(erase, key, 7, new TransactionSequence(lastWriter));
            erase.Commit();
        }
        int count = BTreeCursor.BatchSize;
        while (await cursor.MoveNextAsync()) { cursor.CurrentEntryReference.ShouldBe(7UL); count++; }

        // Assert
        count.ShouldBe(600);
        cursor.EntriesExamined.ShouldBe(600);
        (await cursor.MoveNextAsync()).ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => cursor.CurrentKey);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Indexing] - Cursor: old snapshots resume after splits, tombstones and physical rollback (#1542)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MoveNextAsync_SplitsAndRollbackBetweenBatches_ShouldKeepOldSnapshot(bool reverse)
    {
        // Arrange
        await using var harness = new IndexTestHarness();
        var index = await CreateAsync(harness);
        var seed = await harness.BeginAsync();
        for (int i = 0; i < 400; i++) { await index.InsertAsync(seed, WideKey(i), (ulong)i); }
        await harness.CommitAsync(seed);
        var reader = await harness.Manager.BeginAsync();
        await using var cursor = index.OpenCursor(reader.Snapshot, IndexKeyRange.All, reverse);
        var found = new List<ulong>();
        for (int i = 0; i < 64; i++) { (await cursor.MoveNextAsync()).ShouldBeTrue(); found.Add(cursor.CurrentEntryReference); }

        // Act: grow the root while the cursor is open, then restore all physical positions.
        var doomed = await harness.BeginAsync();
        for (int i = 0; i < 3_000; i++) { await index.InsertAsync(doomed, WideKey(i / 8), (ulong)(10_000 + i)); }
        for (int i = 0; i < 64; i++) { (await cursor.MoveNextAsync()).ShouldBeTrue(); found.Add(cursor.CurrentEntryReference); }
        await harness.RollbackAsync(doomed);
        var deleter = await harness.BeginAsync();
        for (int i = 0; i < 400; i++) { await index.DeleteAsync(deleter, WideKey(i), (ulong)i); }
        await harness.CommitAsync(deleter);
        while (await cursor.MoveNextAsync()) { found.Add(cursor.CurrentEntryReference); }

        // Assert: the cursor still sees the committed versions its owner pinned before deletion.
        var expected = Enumerable.Range(0, 400).Select(i => (ulong)i);
        if (reverse) { expected = expected.Reverse(); }
        found.ShouldBe(expected);
        (await ReadAsync(index, reader.Snapshot, IndexKeyRange.All, reverse)).ShouldBe(expected);
        var latest = await harness.Manager.BeginAsync();
        (await ReadAsync(index, latest.Snapshot, IndexKeyRange.All, reverse)).ShouldBeEmpty();
        await harness.Manager.CommitAsync(latest);
        await harness.Manager.CommitAsync(reader);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Indexing] - Cursor: both tokens cancel a blocked latch acquisition and release the batch (#1542)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MoveNextAsync_CanceledWhileWaitingForLatch_ShouldReleaseWithoutWaitingForWriter(bool cancelOpening)
    {
        // Arrange: neither token is canceled initially; the move must combine them while waiting.
        await using var harness = new IndexTestHarness();
        var index = await CreateAsync(harness);
        var reader = await harness.Manager.BeginAsync();
        using var opening = new CancellationTokenSource();
        using var moving = new CancellationTokenSource();
        var cursor = index.OpenCursor(reader.Snapshot, IndexKeyRange.All, cancellationToken: opening.Token);
        using var started = new ManualResetEventSlim();
        Task move;

        // Act: synchronous helper keeps the write latch on the acquiring thread.
        index.Latch.EnterWrite();
        try
        {
            move = DedicatedTestTask.Run(async () =>
            {
                started.Set();
                await Should.ThrowAsync<OperationCanceledException>(async () => await cursor.MoveNextAsync(moving.Token));
            });
            started.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue();
            (cancelOpening ? opening : moving).Cancel();
            SpinWait.SpinUntil(() => move.IsCompleted, TimeSpan.FromSeconds(10)).ShouldBeTrue();
        }
        finally { index.Latch.ExitWrite(); }
        await move;

        // Assert
        cursor.Entries.ShouldBeEmpty();
        cursor.Continuation.ShouldBeNull();
        Should.Throw<ObjectDisposedException>(() => cursor.CurrentEntryReference);
        await cursor.DisposeAsync();
        await harness.Manager.CommitAsync(reader);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Indexing] - Cursor: cancellation bounds invisible-entry work and drops retained state (#1542)")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MoveNextAsync_InvisibleRunCanceledBetweenBatches_ShouldStopAtWorkLimit(bool reverse, bool cancelOpening)
    {
        // Arrange
        await using var harness = new IndexTestHarness();
        var index = await CreateAsync(harness);
        var seed = await harness.BeginAsync();
        for (int i = 0; i < 2_000; i++) { await index.InsertAsync(seed, IndexKey.FromInt64(i), (ulong)i); }
        await harness.CommitAsync(seed);
        using var stop = new CancellationTokenSource();
        using var moving = new CancellationTokenSource();
        var snapshot = new TransactionSnapshot(new TransactionSequence(100), TransactionSequence.None, seed.Sequence, []);
        var cursor = index.OpenCursor(snapshot, IndexKeyRange.All, reverse, cancellationToken: stop.Token);
        int batches = 0;
        cursor.BatchRead = () => { batches++; (cancelOpening ? stop : moving).Cancel(); };

        // Act / Assert: an all-invisible range must yield the latch at the examination bound.
        await Should.ThrowAsync<OperationCanceledException>(async () => await cursor.MoveNextAsync(moving.Token));
        batches.ShouldBe(1);
        cursor.EntriesExamined.ShouldBe(BTreeCursor.ExaminationLimit);
        cursor.Entries.ShouldBeEmpty();
        index.Latch.EnterWrite();
        index.Latch.ExitWrite();
        await cursor.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - Cursor: opening, buffered reads and disposal observe cancellation and lifetime (#1542)")]
    public async Task OpenCursor_CancellationAndEarlyDisposal_ShouldReleaseOwnedState()
    {
        // Arrange
        await using var harness = new IndexTestHarness();
        var index = await CreateAsync(harness);
        var writer = await harness.BeginAsync();
        for (int i = 0; i < 100; i++) { await index.InsertAsync(writer, IndexKey.FromInt64(i), (ulong)i); }
        await harness.CommitAsync(writer);
        var reader = await harness.Manager.BeginAsync();
        using var stop = new CancellationTokenSource();
        var cursor = index.OpenCursor(reader, IndexKeyRange.All, cancellationToken: stop.Token);
        Should.Throw<InvalidOperationException>(() => cursor.CurrentEntryReference);
        (await cursor.MoveNextAsync()).ShouldBeTrue();
        IndexKey retained = cursor.CurrentKey;
        stop.Cancel();

        // Act / Assert: cancellation also applies to already buffered entries.
        await Should.ThrowAsync<OperationCanceledException>(async () => await cursor.MoveNextAsync());
        Should.Throw<OperationCanceledException>(() => index.OpenCursor(reader, IndexKeyRange.All, cancellationToken: stop.Token));
        Should.Throw<OperationCanceledException>(() => index.OpenCursor(reader.Snapshot, IndexKeyRange.All, cancellationToken: stop.Token));
        cursor.Entries.ShouldBeEmpty();
        retained.ShouldBe(IndexKey.FromInt64(0));
        await cursor.DisposeAsync();
        await cursor.DisposeAsync();
        await Should.ThrowAsync<ObjectDisposedException>(async () => await cursor.MoveNextAsync());

        var early = index.OpenCursor(reader.Snapshot, IndexKeyRange.All);
        (await early.MoveNextAsync()).ShouldBeTrue();
        await early.DisposeAsync();
        early.Entries.ShouldBeEmpty();
        early.Continuation.ShouldBeNull();
        await harness.Manager.CommitAsync(reader);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Indexing] - Cursor: corrupt-page exceptions release partial batches, pins and the read latch (#1542)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MoveNextAsync_CorruptPage_ShouldReleaseBeforeRollback(bool corruptRoot)
    {
        // Arrange: damage the root inside a reversible bracket.
        await using var harness = new IndexTestHarness();
        var index = await CreateAsync(harness);
        var seed = await harness.BeginAsync();
        for (int i = 0; i < 200; i++) { await index.InsertAsync(seed, WideKey(i), (ulong)i); }
        await harness.CommitAsync(seed);
        var reader = await harness.Manager.BeginAsync();
        using var damage = harness.Storage.BeginTransaction();
        index.Latch.EnterWrite();
        try
        {
            long pageId = index.RootPageId;
            if (!corruptRoot)
            {
                while (true)
                {
                    using var page = harness.Storage.PageManager.GetPage((PageId)pageId);
                    var node = new BTreeNode(page.Page.AsBodySpan());
                    if (node.IsLeaf) { pageId = node.NextLeaf; break; }
                    pageId = node.LeftmostChild;
                }
            }
            using var handle = harness.Storage.OpenPageForWrite(damage, (PageId)pageId, index.Latch);
            handle.Page.AsBodySpan()[0] = 0;
            handle.MarkDirty();
        }
        finally { index.Latch.ExitWrite(); }
        var cursor = index.OpenCursor(reader.Snapshot, IndexKeyRange.All);

        // Act
        await Should.ThrowAsync<IndexCorruptionException>(async () =>
        {
            while (await cursor.MoveNextAsync()) { }
        });
        if (!corruptRoot) { cursor.EntriesExamined.ShouldBeGreaterThan(0); }
        damage.Rollback(); // Must acquire the write latch: a leaked read hold fails here.

        // Assert
        cursor.Entries.ShouldBeEmpty();
        await cursor.DisposeAsync();
        (await ReadAsync(index, reader.Snapshot, IndexKeyRange.All, false)).Count.ShouldBe(200);
        await harness.Manager.CommitAsync(reader);
    }

    private static async Task<BTreeIndex> CreateAsync(IndexTestHarness harness)
    {
        var setup = await harness.BeginAsync();
        var index = await harness.IndexManager.CreateIndexAsync(setup, 1, new IndexDefinition("ix_incremental"));
        await harness.CommitAsync(setup);
        return index;
    }

    private static async Task<List<ulong>> ReadAsync(BTreeIndex index, TransactionSnapshot snapshot, IndexKeyRange range, bool reverse)
    {
        var found = new List<ulong>();
        await using var cursor = index.OpenCursor(snapshot, range, reverse);
        while (await cursor.MoveNextAsync()) { found.Add(cursor.CurrentEntryReference); }
        return found;
    }

    private static IndexKey WideKey(int value)
    {
        byte[] bytes = new byte[508];
        bytes.AsSpan(0, 500).Fill(46);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(500), (ulong)value);
        return new IndexKey(bytes);
    }
}
