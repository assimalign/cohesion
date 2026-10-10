using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Indexing.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Indexing.Tests;

/// <summary>
/// A rolled-back storage transaction restores the tree's pages in place, each cleared and then
/// rewritten from its pre-image. Until #1371 it did so outside the tree latch, so a cursor reading
/// the tree meanwhile could read a cleared leaf (an entry with reference zero, which a SQL seek
/// followed to page 0, the file header), miss committed entries, or reach a root that was no node.
/// Every page write of the tree now enlists the tree latch with its storage transaction, and the
/// rollback restores while it holds that latch exclusively.
/// </summary>
public sealed class BTreeRollbackLatchTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - Rollback: a rolled-back root split is restored only once the tree's reader has left (#1371)")]
    public async Task Rollback_WhileACursorHoldsTheTreeLatch_ShouldRestoreOnlyAfterTheCursorLeaves()
    {
        // Arrange: a committed one-leaf tree, which a transaction grows two levels.
        await using var harness = new IndexTestHarness();
        var index = await CreateIndexAsync(harness);
        var setup = await harness.BeginAsync();
        for (long i = 0; i < 10; i++)
        {
            await index.InsertAsync(setup, WideKey(i), (ulong)i);
        }

        await harness.CommitAsync(setup);
        byte[] committedRoot = RootBody(harness, index);

        var doomed = await harness.BeginAsync();
        for (long i = 100; i < 3_100; i++)
        {
            await index.InsertAsync(doomed, WideKey(i), (ulong)i);
        }

        byte[] grownRoot = RootBody(harness, index);
        grownRoot.ShouldNotBe(committedRoot);

        // Act: the rollback starts while a reader holds the tree latch.
        var (heldRoot, completedWhileHeld, rollback) = RollBackWhileReading(harness, index, harness.GetStorageTransaction(doomed));
        await rollback.WaitAsync(Wait);
        await harness.Manager.RollbackAsync(doomed);

        // Assert: for as long as the reader held the latch, the rollback waited and the root was
        // the grown tree's; the restore came after the reader left.
        completedWhileHeld.ShouldBeFalse();
        heldRoot.ShouldBe(grownRoot);
        RootBody(harness, index).ShouldBe(committedRoot);
        var reader = await harness.Manager.BeginAsync();
        (await ReferencesAsync(index, reader)).ShouldBe(Enumerable.Range(0, 10).Select(i => (ulong)i));
        await harness.Manager.CommitAsync(reader);
    }

    /// <summary>
    /// A writer grows the tree in a transaction and rolls it back, over and over, while cursors
    /// read the whole tree. Every cursor must read exactly the committed entries. Against the code
    /// before #1371 the cursors read torn trees: missing entries, entries of reference zero, or a
    /// page that is no node (<see cref="IndexCorruptionException"/>).
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - Rollback: cursors racing rolled-back splits read exactly the committed entries (#1371)")]
    public async Task Cursor_RacingRolledBackSplits_ShouldReadExactlyTheCommittedEntries()
    {
        // Arrange: 400 committed entries over several leaves.
        await using var harness = new IndexTestHarness();
        var index = await CreateIndexAsync(harness);
        var setup = await harness.BeginAsync();
        for (long i = 0; i < 400; i++)
        {
            await index.InsertAsync(setup, IndexKey.FromInt64(i), (ulong)i);
        }

        await harness.CommitAsync(setup);
        var committed = Enumerable.Range(0, 400).Select(i => (ulong)i).ToArray();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Exception? failure = null;
        long rollbacks = 0;
        long reads = 0;

        // Act: the writer inserts a second version of every committed key, which splits leaves
        // across the whole tree, then rolls the transaction back physically.
        var writer = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested && Volatile.Read(ref failure) is null)
            {
                var doomed = await harness.BeginAsync();
                for (long i = 0; i < 400; i++)
                {
                    await index.InsertAsync(doomed, IndexKey.FromInt64(i), 1_000_000UL + (ulong)i);
                }

                await harness.RollbackAsync(doomed);
                Interlocked.Increment(ref rollbacks);
            }
        });
        var readers = Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested && Volatile.Read(ref failure) is null)
            {
                var reader = await harness.Manager.BeginAsync();
                try
                {
                    var references = await ReferencesAsync(index, reader);
                    if (!references.SequenceEqual(committed))
                    {
                        Interlocked.CompareExchange(ref failure, new InvalidOperationException(
                            $"A cursor read {references.Count} entries, not the 400 committed ones: [{string.Join(", ", references.Except(committed).Take(5))}] extra."), null);
                    }

                    Interlocked.Increment(ref reads);
                }
                catch (Exception exception)
                {
                    Interlocked.CompareExchange(ref failure, exception, null);
                }
                finally
                {
                    await harness.Manager.CommitAsync(reader);
                }
            }
        })).ToArray();
        await Task.WhenAll([writer, .. readers]);

        // Assert
        failure.ShouldBeNull();
        rollbacks.ShouldBeGreaterThan(0);
        reads.ShouldBeGreaterThan(0);
    }

    /// <summary>
    /// Holds the tree latch shared, as a cursor does while it reads, starts the rollback on another
    /// thread, gives it half a second, which a rollback that does not wait for the latch needs a
    /// few milliseconds of, and reads the root page under the hold. Synchronous, so the hold is
    /// taken and released on one thread. (The storage's own test observes the rollback queue on the
    /// latch; this assembly cannot see the latch's waiters.)
    /// </summary>
    private static (byte[] HeldRoot, bool CompletedWhileHeld, Task Rollback) RollBackWhileReading(IndexTestHarness harness, BTreeIndex index, StorageTransaction transaction)
    {
        index.Latch.EnterRead();
        try
        {
            var rollback = Task.Run(transaction.Rollback);
            bool completed = SpinWait.SpinUntil(() => rollback.IsCompleted, TimeSpan.FromMilliseconds(500));
            return (RootBody(harness, index), completed, rollback);
        }
        finally
        {
            index.Latch.ExitRead();
        }
    }

    private static async Task<BTreeIndex> CreateIndexAsync(IndexTestHarness harness)
    {
        var setup = await harness.BeginAsync();
        var index = await harness.IndexManager.CreateIndexAsync(setup, objectId: 1, new IndexDefinition("ix_rollback", IndexKind.BTree, false));
        await harness.CommitAsync(setup);
        return index;
    }

    private static async Task<List<ulong>> ReferencesAsync(BTreeIndex index, TransactionContext reader)
    {
        var references = new List<ulong>();
        await using var cursor = index.OpenCursor(reader, IndexKeyRange.All);
        while (await cursor.MoveNextAsync())
        {
            references.Add(cursor.CurrentEntryReference);
        }

        return references;
    }

    private static byte[] RootBody(IndexTestHarness harness, BTreeIndex index)
    {
        using var handle = harness.Storage.PageManager.GetPage((PageId)index.RootPageId);
        return handle.Page.AsBodySpan().ToArray();
    }

    /// <summary>
    /// A 508-byte key ending in the order-preserving value: about fourteen fit a leaf, so a few
    /// thousand of them split the root as a leaf and again as an internal node.
    /// </summary>
    private static IndexKey WideKey(long value)
    {
        var bytes = new byte[508];
        bytes.AsSpan(0, 500).Fill(0x2E);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(500), (ulong)value ^ 0x8000_0000_0000_0000UL);
        return new IndexKey(bytes);
    }
}
