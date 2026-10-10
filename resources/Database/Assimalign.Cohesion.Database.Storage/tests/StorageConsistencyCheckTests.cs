using System;
using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage.Units;

namespace Assimalign.Cohesion.Database.Storage.Tests;

/// <summary>
/// The debug consistency check (#1253, PostgreSQL's <c>wal_consistency_checking</c>): every page
/// record the storage journals is replayed onto a shadow of the page, as recovery would, and the
/// shadow is compared with the pooled page after every commit and rollback, at every first touch
/// that journals no image, and at every checkpoint. A page not imaged since the checkpoint has no
/// shadow: the audit of changes made outside a storage transaction refuses its write-back while it
/// is dirty, and compares it with the data file's copy at the touch that images it.
/// </summary>
public sealed class StorageConsistencyCheckTests
{
    [Fact(DisplayName = "Cohesion Test [Storage] - Consistency check: a mixed workload with steals, rollbacks and checkpoints matches its replay")]
    public void ConsistencyCheck_MixedWorkload_ShouldFindNoDifference()
    {
        // Arrange
        using var storage = TornStorage.Create(poolCapacity: 3, consistencyChecks: true);
        var random = new Random(1253);
        var rows = Enumerable.Range(0, 24).Select(i => storage.Insert($"row {i}".PadRight(1500, '.'))).ToList();

        // Act
        for (int round = 0; round < 40; round++)
        {
            using var transaction = storage.BeginTransaction();
            for (int i = 0; i < 4; i++)
            {
                var (pageId, slot) = rows[random.Next(rows.Count)];
                storage.Update(transaction, pageId, slot, $"round {round}.{i}".PadRight(1500, (char)('a' + random.Next(26))));
            }

            if (round % 5 == 0)
            {
                storage.PageManager.FlushAll();
            }

            if (round % 3 == 0)
            {
                transaction.Rollback();
            }
            else
            {
                transaction.Commit();
            }

            if (round % 7 == 0)
            {
                storage.Checkpoint();
            }
        }

        // Assert
        storage.ConsistencyCheck.ShouldNotBeNull();
        storage.ConsistencyCheck.Checks.ShouldBeGreaterThan(100);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Consistency check: a page changed outside a storage transaction is caught at its next touch")]
    public unsafe void ConsistencyCheck_PageChangedOutsideATransaction_ShouldFailTheNextTouch()
    {
        // Arrange: a page imaged in this checkpoint interval, then changed through a pinned handle
        // with no storage transaction: no delta will ever carry the change.
        var storage = TornStorage.Create(consistencyChecks: true); // abandoned: its close would checkpoint the changed page
        var (pageId, slot) = storage.Insert("row");
        using (var handle = storage.PageManager.GetPage(pageId))
        {
            handle.Page.Pointer[Page.Size - 100] ^= 0xFF;
            handle.MarkDirty();
        }

        var transaction = storage.BeginTransaction();

        // Act
        var failure = Should.Throw<InvalidOperationException>(() => storage.Update(transaction, pageId, slot, "new"));

        // Assert: the touch failed before anything was journaled, and left the page unlocked.
        failure.Message.ShouldContain($"page {(long)pageId}");
        failure.Message.ShouldContain("changed outside a storage transaction");
        failure.Message.ShouldContain($"offset {Page.Size - 100}");
        storage.IsPageWriteLocked(pageId).ShouldBeFalse();
        transaction.Rollback();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Consistency check: a change outside a storage transaction is caught at the checkpoint")]
    public unsafe void ConsistencyCheck_PageChangedOutsideATransaction_ShouldFailTheCheckpoint()
    {
        // Arrange
        using var storage = TornStorage.Create(consistencyChecks: true);
        var (pageId, _) = storage.Insert("row");
        using (var handle = storage.PageManager.GetPage(pageId))
        {
            handle.Page.Pointer[Page.HeaderSize + 1] ^= 0xFF;
            handle.MarkDirty();
        }

        // Act
        var failure = Should.Throw<InvalidOperationException>(() => storage.Checkpoint());

        // Assert
        failure.Message.ShouldContain($"page {(long)pageId}");
        failure.Message.ShouldContain("at the checkpoint");
    }

    /// <summary>
    /// A page whose last record is older than the checkpoint has no shadow. A change made to it
    /// outside a storage transaction and marked dirty would reach the data file through the next
    /// write-back, where no journal record describes it (#1253 review, probe A1): the write-back of
    /// a dirty page at or below the redo point is refused.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Storage] - Consistency check: a change outside a transaction to a page not imaged since the checkpoint is caught at its write-back")]
    public unsafe void ConsistencyCheck_UnimagedPageChangedOutsideATransaction_ShouldFailItsWriteBack()
    {
        // Arrange
        var storage = TornStorage.Create(consistencyChecks: true); // abandoned: its close would checkpoint the changed page
        var (pageId, _) = storage.Insert("row");
        storage.Checkpoint();
        using (var handle = storage.PageManager.GetPage(pageId))
        {
            handle.Page.Pointer[Page.Size - 100] ^= 0xFF;
            handle.MarkDirty();
        }

        // Act
        var failure = Should.Throw<InvalidOperationException>(() => storage.Checkpoint());

        // Assert
        failure.Message.ShouldContain($"page {(long)pageId}");
        failure.Message.ShouldContain($"at or below the redo point {storage.RedoLsn}");
        failure.Message.ShouldContain("changed outside a storage transaction since the last checkpoint");
    }

    /// <summary>
    /// The touch that images a page with no record since the checkpoint would journal whatever the
    /// pool holds as committed content, a change made outside a storage transaction included
    /// (#1253 review, probe A2): it compares the pool's copy with the data file's first, which also
    /// catches a change never marked dirty.
    /// </summary>
    [Theory(DisplayName = "Cohesion Test [Storage] - Consistency check: a change outside a transaction to a page not imaged since the checkpoint is caught at the touch that images it")]
    [InlineData(true)]
    [InlineData(false)]
    public unsafe void ConsistencyCheck_UnimagedPageChangedOutsideATransaction_ShouldFailTheImagingTouch(bool markDirty)
    {
        // Arrange
        var storage = TornStorage.Create(consistencyChecks: true); // abandoned: its close would checkpoint the changed page
        var (pageId, slot) = storage.Insert("row");
        storage.Checkpoint();
        long checkpointLsn = storage.Log.LastLsn;
        using (var handle = storage.PageManager.GetPage(pageId))
        {
            handle.Page.Pointer[Page.Size - 100] ^= 0xFF;
            if (markDirty)
            {
                handle.MarkDirty();
            }
        }

        var transaction = storage.BeginTransaction();

        // Act
        var failure = Should.Throw<InvalidOperationException>(() => storage.Update(transaction, pageId, slot, "new"));

        // Assert: refused before the image was journaled, and the page left unlocked.
        failure.Message.ShouldContain($"page {(long)pageId}");
        failure.Message.ShouldContain("changed outside a storage transaction since the last checkpoint");
        failure.Message.ShouldContain($"offset {Page.Size - 100}");
        failure.Message.ShouldContain("the data file holds");
        storage.Log.ReadAll().ShouldNotContain(record => record.Type == JournalRecordType.FullPageImage && record.Lsn > checkpointLsn);
        storage.IsPageWriteLocked(pageId).ShouldBeFalse();
        transaction.Rollback();
    }

    /// <summary>
    /// The pages the storage writes outside the journal on purpose — a new file set's first page,
    /// checkpoint anchor pages as their chains grow and shrink, the page manager's raw allocation
    /// and free, and a bracket's allocation of a page an anchor chain freed — are exempt from the
    /// audit of pages at or below the redo point.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Storage] - Consistency check: pages the storage writes outside the journal on purpose are not reported")]
    public void ConsistencyCheck_PagesWrittenOutsideTheJournalOnPurpose_ShouldNotBeReported()
    {
        // Arrange: an anchor long enough to need a chain of pages in each header slot.
        var storage = TornStorage.Create(consistencyChecks: true);
        long[] anchor = [.. Enumerable.Range(1, 1_500).Select(i => (long)i)];
        storage.Insert("row");

        // Act
        storage.Checkpoint(anchor);
        storage.FlushHeader();
        storage.Checkpoint(anchor);
        storage.Checkpoint(anchor.AsSpan(0, 10));
        storage.Checkpoint(anchor.AsSpan(0, 10));
        long raw;
        using (var handle = storage.PageManager.AllocatePage(PageType.Data))
        {
            raw = (long)handle.Id;
        }

        storage.Checkpoint();
        storage.PageManager.FreePage((PageId)raw);
        storage.Checkpoint();
        var pages = storage.FillPages(4);
        storage.Checkpoint();
        storage.Dispose();

        // Assert
        storage.ConsistencyCheck!.Checks.ShouldBeGreaterThan(0);
        pages.Length.ShouldBe(4);
    }

    /// <summary>
    /// An open that defers its checkpoint leaves recovered pages above the redo point with no image
    /// the process journaled: the check rebuilds their shadows from the journal itself, so the
    /// recovered pages and the deltas chained onto them are checked too.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Storage] - Consistency check: pages recovered without a checkpoint are checked against the journal")]
    public void ConsistencyCheck_RecoveredPagesWithoutACheckpoint_ShouldRebuildShadowsFromTheJournal()
    {
        // Arrange
        var crashed = TornStorage.Create(); // abandoned: a crash
        var rows = Enumerable.Range(0, 6).Select(i => crashed.Insert($"row {i}".PadRight(2500, '.'))).ToList();
        var images = crashed.CaptureDurable();
        var data = new CrashSimulationStream(images.Data, writeThrough: true);
        var journal = new CrashSimulationStream(images.Journal, writeThrough: true);

        // Act
        using var recovered = TornStorage.Open(data, journal, consistencyChecks: true);
        foreach (var (pageId, slot) in rows)
        {
            using var transaction = recovered.BeginTransaction();
            recovered.Update(transaction, pageId, slot, "updated");
            transaction.Commit();
        }

        recovered.Checkpoint();

        // Assert
        recovered.ConsistencyCheck!.Checks.ShouldBeGreaterThanOrEqualTo(2 * rows.Count);
        rows.ShouldAllBe(row => recovered.Read(row.PageId, row.SlotIndex) == "updated");
    }

    /// <summary>
    /// Invariant P: a page above the redo point has a full page image in the journal. A page whose
    /// LSN was raised outside the journal breaks it — its next delta would have nothing to chain
    /// onto — and the check names the invariant when it rebuilds the page's shadow.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Storage] - Consistency check: a page above the redo point with no image in the journal breaks invariant P")]
    public void ConsistencyCheck_PageAboveTheRedoPointWithoutAnImage_ShouldNameInvariantP()
    {
        // Arrange: a page last changed before the checkpoint, whose LSN is then raised in the pool
        // while the check is off, so no shadow of it exists.
        using var storage = TornStorage.Create();
        var (pageId, slot) = storage.Insert("row");
        storage.Checkpoint();
        using (var handle = storage.PageManager.GetPage(pageId))
        {
            var page = handle.Page;
            page.Lsn = storage.RedoLsn + 1;
            handle.MarkDirty();
        }

        storage.EnableConsistencyChecks();
        var transaction = storage.BeginTransaction();

        // Act
        var failure = Should.Throw<InvalidOperationException>(() => storage.Update(transaction, pageId, slot, "new"));

        // Assert
        failure.Message.ShouldContain("invariant P");
        transaction.Rollback();
    }
}
