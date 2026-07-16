using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Indexing.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Indexing.Tests;

/// <summary>
/// Tests for the maintenance surfaces added for model-engine consumers (#912):
/// the offline build path that preserves version stamps, the physical undo
/// operations (erase an aborted insert, restore an aborted tombstone), and the
/// open-time aborted-writer purge walk.
/// </summary>
public class BTreeMaintenanceTests
{
    private static async Task<(IndexTestHarness Harness, IIndex Index)> CreateIndexAsync(bool unique = false)
    {
        var harness = new IndexTestHarness();
        var setup = await harness.BeginAsync();
        var index = await harness.IndexManager.CreateIndexAsync(
            setup, objectId: 1, new IndexDefinition("ix_maintenance", IndexKind.BTree, unique));
        await harness.CommitAsync(setup);
        return (harness, index);
    }

    private static async Task<List<ulong>> VisibleReferencesAsync(IndexTestHarness harness, IIndex index)
    {
        var reader = await harness.BeginAsync();

        try
        {
            var references = new List<ulong>();
            await using var cursor = index.OpenCursor(reader, IndexKeyRange.All);

            while (await cursor.MoveNextAsync())
            {
                references.Add(cursor.CurrentEntryReference);
            }

            return references;
        }
        finally
        {
            await harness.RollbackAsync(reader);
        }
    }

    /// <summary>
    /// Advances the manager's committed horizon past the given sequence values by
    /// beginning and committing transactions, so explicit stamps at those values
    /// read as committed history.
    /// </summary>
    private static async Task<TransactionSequence> AdvanceCommittedHorizonAsync(IndexTestHarness harness, int transactions)
    {
        TransactionSequence last = default;

        for (int i = 0; i < transactions; i++)
        {
            var context = await harness.BeginAsync();
            last = context.Sequence;
            await harness.CommitAsync(context);
        }

        return last;
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - Maintenance: the build path preserves writer and deleter stamps")]
    public async Task InsertVersionAsync_WithExplicitStamps_ShouldPreserveVisibility()
    {
        // Arrange: two committed sequences form history; a third value is an
        // in-flight (never-committed) writer.
        var (harness, index) = await CreateIndexAsync();
        await using var harnessLifetime = harness;

        var committedWriter = await AdvanceCommittedHorizonAsync(harness, 1);
        var committedDeleter = await AdvanceCommittedHorizonAsync(harness, 1);
        var unprovenWriter = new TransactionSequence(999);

        // Act: an offline build inserting three historical versions verbatim.
        using (var bracket = harness.Storage.BeginTransaction())
        {
            await index.InsertVersionAsync(bracket, IndexKey.FromInt64(1), 10, committedWriter, TransactionSequence.None);
            await index.InsertVersionAsync(bracket, IndexKey.FromInt64(2), 20, committedWriter, committedDeleter);
            await index.InsertVersionAsync(bracket, IndexKey.FromInt64(3), 30, unprovenWriter, TransactionSequence.None);
            bracket.Commit();
        }

        // Assert: the live version is visible; the committed tombstone reads as
        // absence; the unproven writer's version is invisible.
        (await VisibleReferencesAsync(harness, index)).ShouldBe(new[] { 10UL });
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - Maintenance: erase removes an aborted insert, clear-deleter restores an aborted tombstone")]
    public async Task EraseAndClearDeleter_MatchingStamps_ShouldUndoPhysically()
    {
        // Arrange: one live committed entry and one entry from writer W; W also
        // tombstoned the committed entry — then W aborts.
        var (harness, index) = await CreateIndexAsync();
        await using var harnessLifetime = harness;

        var committed = await AdvanceCommittedHorizonAsync(harness, 1);
        var aborted = new TransactionSequence(500);

        using (var bracket = harness.Storage.BeginTransaction())
        {
            await index.InsertVersionAsync(bracket, IndexKey.FromInt64(1), 10, committed, TransactionSequence.None);
            await index.InsertVersionAsync(bracket, IndexKey.FromInt64(2), 20, aborted, TransactionSequence.None);
            bracket.Commit();
        }

        using (var tombstone = harness.Storage.BeginTransaction())
        {
            // Stamp the committed entry's deleter with the aborting writer, as an
            // update statement would.
            await index.InsertVersionAsync(tombstone, IndexKey.FromInt64(3), 30, committed, aborted);
            tombstone.Commit();
        }

        // Act: the logical undo — erase W's insert, clear W's tombstone. A
        // mismatched stamp must be a no-op.
        using (var undo = harness.Storage.BeginTransaction())
        {
            await index.EraseAsync(undo, IndexKey.FromInt64(2), 20, aborted);
            await index.EraseAsync(undo, IndexKey.FromInt64(1), 10, aborted); // wrong writer: no-op
            await index.ClearDeleterAsync(undo, IndexKey.FromInt64(3), 30, aborted);
            undo.Commit();
        }

        // Assert: W's insert is gone, the tombstoned entry is live again, and the
        // untouched entry survived the mismatched erase.
        (await VisibleReferencesAsync(harness, index)).ShouldBe(new[] { 10UL, 30UL });
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - Maintenance: the purge walk scrubs every unproven writer in one pass")]
    public async Task PurgeWritersAsync_UnprovenWriters_ShouldScrubEntriesAndTombstones()
    {
        // Arrange: committed history interleaved with two unproven writers'
        // stamps, spread across enough entries to span multiple leaves.
        var (harness, index) = await CreateIndexAsync();
        await using var harnessLifetime = harness;

        var committed = await AdvanceCommittedHorizonAsync(harness, 1);
        var unprovenA = new TransactionSequence(900);
        var unprovenB = new TransactionSequence(901);

        using (var bracket = harness.Storage.BeginTransaction())
        {
            for (long i = 0; i < 600; i++)
            {
                // Every third entry belongs to an unproven writer; every fifth
                // committed entry carries an unproven tombstone.
                var writer = i % 3 == 0 ? unprovenA : committed;
                var deleter = writer == committed && i % 5 == 0 ? unprovenB : TransactionSequence.None;
                await index.InsertVersionAsync(bracket, IndexKey.FromInt64(i), (ulong)i, writer, deleter);
            }

            bracket.Commit();
        }

        // Act
        long purged;
        using (var scrub = harness.Storage.BeginTransaction())
        {
            purged = await harness.IndexManager.PurgeWritersAsync(
                scrub, new HashSet<TransactionSequence> { unprovenA, unprovenB });
            scrub.Commit();
        }

        // Assert: every committed entry is visible (tombstones cleared), every
        // unproven entry is gone.
        purged.ShouldBeGreaterThan(0);
        var visible = await VisibleReferencesAsync(harness, index);
        visible.Count.ShouldBe(400); // 600 minus the 200 unprovenA inserts
        visible.ShouldNotContain(0UL);   // i = 0 belonged to unprovenA
        visible.ShouldContain(10UL);     // i = 10: committed with an unprovenB tombstone, restored
    }
}
