using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage.Units;

namespace Assimalign.Cohesion.Database.Storage.Tests;

[Collection(StorageRaceCollection.Name)]
public sealed class StorageStaleTouchTests
{
    [Theory(DisplayName = "Cohesion Test [Storage] - Stale insert: reuse after reading the current page leaves the replacement owner writable")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task InsertRecord_ReuseAfterOwnerRead_ShouldPreserveReplacement(bool replacementCommitted, bool sameOwner)
    {
        // Arrange
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        // Act / Assert
        await ExerciseOwnerReadRaceAsync(insert: true, index: false, replacementCommitted, sameOwner, stop.Token);
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Stale owner release: reuse after the directory snapshot preserves data and index replacements")]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task FreeOwnerPages_ReuseAfterOwnerRead_ShouldPreserveReplacement(bool index, bool replacementCommitted, bool sameOwner)
    {
        // Arrange
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        // Act / Assert
        await ExerciseOwnerReadRaceAsync(insert: false, index, replacementCommitted, sameOwner, stop.Token);
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Allocation: a failed first touch releases its reservation and owner-directory entry")]
    [InlineData(false)]
    [InlineData(true)]
    public void AllocatePageForWrite_FirstTouchFails_ShouldReleaseReservationAndDirectory(bool index)
    {
        // Arrange: fail after the image append, before RegisterTouch can record a pre-image.
        using var storage = TornStorage.Create(consistencyChecks: true);
        using var transaction = storage.BeginTransaction();
        int failures = 0;
        storage.Log.ConfigureCheckpointTrigger(1, () =>
        {
            failures++;
            throw new InvalidOperationException("Injected first-touch failure.");
        });

        // Act
        try
        {
            Should.Throw<InvalidOperationException>(() =>
            {
                if (index)
                {
                    using var page = storage.AllocatePageForWrite(transaction, PageType.Index, ownerId: 17);
                }
                else
                {
                    storage.Insert(transaction, 17, [1]);
                }
            }).Message.ShouldBe("Injected first-touch failure.");
        }
        finally
        {
            storage.Log.ConfigureCheckpointTrigger(0, null);
        }

        // Assert: both directories are empty, the id is reusable, and the failed pin is gone.
        failures.ShouldBe(1);
        var failed = (PageId)(storage.FreeSpaceMap.TotalPageCount - 1);
        storage.FreeSpaceMap.IsAllocated(failed).ShouldBeFalse();
        storage.HasOwnerPages(17).ShouldBeFalse();
        storage.GetOwnerPages(17).ShouldBeEmpty();
        storage.FreeOwnerPages(transaction, 17).ShouldBe(0);
        transaction.PreImages.ShouldBeEmpty();
        storage.IsPageWriteLocked(failed).ShouldBeFalse();
        storage.BufferPool.TryGet(failed, out var pin).ShouldBeTrue();
        using (pin)
        {
            pin.ShouldNotBeNull();
            pin.PinCount.ShouldBe(1);
            pin.Page.Type.ShouldBe(PageType.Reclaimed);
        }
        transaction.Rollback();
        using (var retry = storage.BeginTransaction())
        {
            storage.Insert(retry, 99, [7]).PageId.ShouldBe(failed);
            retry.Commit();
        }
        storage.BufferPool.CheckInvariants();
        using var recovered = TornStorage.Open(storage.CaptureDurable());
        recovered.ScanOwner(17).ShouldBeEmpty();
        recovered.ScanOwner(99).Single().ShouldBe(new byte[] { 7 });
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Free generations: retain reuse protection until the last active bracket ends")]
    public void Commit_LastActiveBracketEnds_ShouldResetFreeGenerations()
    {
        // Arrange
        using var storage = TornStorage.Create();
        var row = storage.Insert("original");
        using var observer = storage.BeginTransaction();
        storage.FreeSpaceMap.TryGetAllocationGeneration(row.PageId, out long learned).ShouldBeTrue();

        // Act: an active observer prevents pruning even after another bracket's committed reuse.
        using (var freeing = storage.BeginTransaction())
        {
            storage.FreeOwnerPages(freeing, 0).ShouldBe(1);
            freeing.Commit();
        }
        using (var replacement = storage.BeginTransaction())
        {
            storage.Insert(replacement, 99, [7]).PageId.ShouldBe(row.PageId);
            replacement.Commit();
        }

        // Assert
        storage.FreeSpaceMap.IsAllocationCurrent(row.PageId, learned).ShouldBeFalse();
        storage.FreeSpaceMap.TryGetAllocationGeneration(row.PageId, out long retained).ShouldBeTrue();
        retained.ShouldBeGreaterThan(learned);
        observer.Commit();
        storage.FreeSpaceMap.TryGetAllocationGeneration(row.PageId, out long idle).ShouldBeTrue();
        idle.ShouldBe(0);
        storage.ReadBytes(row.PageId, 0).ShouldBe(new byte[] { 7 });
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Free generations: opening free pages starts at generation zero")]
    public void OpenExisting_FreePages_ShouldUseZeroGenerationUntilFreedAgain()
    {
        // Arrange
        using var storage = TornStorage.Create();
        var row = storage.Insert("original");
        using (var freeing = storage.BeginTransaction())
        {
            storage.FreeOwnerPages(freeing, 0).ShouldBe(1);
            freeing.Commit();
        }
        storage.Checkpoint();

        // Act
        using var reopened = TornStorage.Open(storage.CaptureDurable());
        using var replacement = reopened.BeginTransaction();
        reopened.Insert(replacement, 99, [7]).PageId.ShouldBe(row.PageId);

        // Assert
        reopened.FreeSpaceMap.TryGetAllocationGeneration(row.PageId, out long generation).ShouldBeTrue();
        generation.ShouldBe(0);
        replacement.Commit();
        reopened.ScanOwner(99).Single().ShouldBe(new byte[] { 7 });
    }

    private static async Task ExerciseOwnerReadRaceAsync(
        bool insert, bool index, bool replacementCommitted, bool sameOwner, CancellationToken cancellationToken)
    {
        // Arrange: the stale writer learns the old id while its release is still uncommitted.
        using var storage = TornStorage.Create(poolCapacity: 4, durability: StorageCommitDurability.None);
        const ulong originalOwner = 7;
        ulong replacementOwner = sameOwner ? originalOwner : 99;
        PageId original;
        using (var setup = storage.BeginTransaction())
        {
            if (index)
            {
                using var page = storage.AllocatePageForWrite(setup, PageType.Index, originalOwner);
                original = page.Id;
            }
            else
            {
                original = storage.Insert(setup, originalOwner, [1]).PageId;
            }
            setup.Commit();
        }
        using var deleting = storage.BeginTransaction();
        storage.FreeOwnerPages(deleting, originalOwner).ShouldBe(1);
        using var stale = storage.BeginTransaction();
        using var replacement = storage.BeginTransaction();
        using var resume = new ManualResetEventSlim();
        var learned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0;
        int frees = 0;
        int reuses = 0;
        storage.OwnerPageIdsReadForWrite = () =>
        {
            storage.OwnerPageIdsReadForWrite = null;
            Interlocked.Increment(ref reads);
            learned.SetResult();
            resume.Wait(cancellationToken);
        };

        // Act: reuse runs on a different thread, only after the freeing commit returns.
        // The callback never allocates under the freeing thread's reentrant storage locks.
        var writer = Task.Run(() =>
        {
            if (insert)
            {
                storage.Insert(stale, originalOwner, [2]);
                return -1;
            }
            return storage.FreeOwnerPages(stale, originalOwner);
        }, CancellationToken.None);
        int released;
        try
        {
            await learned.Task.WaitAsync(cancellationToken);
            await Task.Run(() =>
            {
                deleting.Commit();
                Interlocked.Increment(ref frees);
                storage.Insert(replacement, replacementOwner, [7]).PageId.ShouldBe(original);
                Interlocked.Increment(ref reuses);
                if (replacementCommitted) { replacement.Commit(); }
            }, CancellationToken.None).WaitAsync(cancellationToken);
        }
        finally
        {
            storage.OwnerPageIdsReadForWrite = null;
            resume.Set();
            released = await writer;
        }

        // Assert: even same-owner reuse is a different allocation. Neither a lock nor a
        // pre-image of the replacement belongs to the stale writer, including before its commit.
        reads.ShouldBe(1);
        frees.ShouldBe(1);
        reuses.ShouldBe(1);
        if (!insert) { released.ShouldBe(0); }
        stale.PreImages.ContainsKey((long)original).ShouldBeFalse();
        stale.PreImages.Count.ShouldBe(insert ? 1 : 0);
        if (replacementCommitted)
        {
            using var update = storage.BeginTransaction();
            storage.Update(update, original, 0, new byte[] { 8 });
            update.Commit();
        }
        else
        {
            storage.Update(replacement, original, 0, new byte[] { 8 });
            replacement.Commit();
        }
        stale.Commit();
        storage.ReadBytes(original, 0).ShouldBe(new byte[] { 8 });
        storage.ScanOwner(replacementOwner).ShouldContain(row => row.SequenceEqual(new byte[] { 8 }));
        using (var update = storage.BeginTransaction())
        {
            storage.Update(update, original, 0, new byte[] { 9 });
            update.Commit();
        }
        storage.ActiveTransactions.ShouldBe(0);
        storage.PreImageBytes.ShouldBe(0);
        storage.IsPageWriteLocked(original).ShouldBeFalse();
        using (var pin = storage.PageManager.GetPage(original)) { pin.PinCount.ShouldBe(1); }
        storage.BufferPool.CheckInvariants();

        using var recovered = TornStorage.Open(storage.CaptureDurable(), poolCapacity: 4);
        recovered.ReadBytes(original, 0).ShouldBe(new byte[] { 9 });
        recovered.ScanOwner(replacementOwner).ShouldContain(row => row.SequenceEqual(new byte[] { 9 }));
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Stale touch: a commit between pin and lock invalidates the old allocation even after reuse")]
    [InlineData(false, false, 0)]
    [InlineData(false, false, 1)]
    [InlineData(false, false, 2)]
    [InlineData(false, true, 0)]
    [InlineData(false, true, 1)]
    [InlineData(false, true, 2)]
    [InlineData(true, false, 0)]
    [InlineData(true, false, 1)]
    [InlineData(true, false, 2)]
    [InlineData(true, true, 0)]
    [InlineData(true, true, 1)]
    [InlineData(true, true, 2)]
    public async Task TouchPage_CommitFreesPinnedPage_ShouldRejectStaleAllocation(bool insert, bool reuse, int completion)
    {
        // Arrange
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        // Act / Assert
        await ExerciseRaceAsync(insert, reuse, completion, stop.Token);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Stale touch regression loop: repeat owner lookup, pin, free, reuse and bracket completion")]
    public async Task TouchPage_RepeatedRaceRegressions_ShouldLeaveNoStaleLocksOrPins()
    {
        // Arrange
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var elapsed = Stopwatch.StartNew();
        (bool Insert, bool Index, bool Committed, bool SameOwner)[] ownerScenarios =
        [
            (true, false, false, false), (true, false, false, true),
            (true, false, true, false), (true, false, true, true),
            (false, false, false, false), (false, false, false, true),
            (false, false, true, false), (false, false, true, true),
            (false, true, false, false), (false, true, false, true),
            (false, true, true, false), (false, true, true, true)
        ];
        var exercised = new int[12 + ownerScenarios.Length];
        int rounds = 0;

        // Act: every round rendezvous after lookup or pin, so a counter proves that window occurred.
        do
        {
            stop.Token.ThrowIfCancellationRequested();
            int scenario = rounds % exercised.Length;
            if (scenario < 12)
            {
                await ExerciseRaceAsync(scenario >= 6, scenario % 6 >= 3, scenario % 3, stop.Token);
            }
            else
            {
                var (insert, index, committed, sameOwner) = ownerScenarios[scenario - 12];
                await ExerciseOwnerReadRaceAsync(insert, index, committed, sameOwner, stop.Token);
            }
            exercised[scenario]++;
            rounds++;
        }
        while (elapsed.Elapsed < TimeSpan.FromSeconds(1) || exercised.Any(count => count < 5));

        // Assert
        exercised.ShouldAllBe(count => count >= 5);
        rounds.ShouldBeGreaterThanOrEqualTo(exercised.Length * 5);
    }

    private static async Task ExerciseRaceAsync(bool insert, bool reuse, int completion, CancellationToken cancellationToken)
    {
        // Arrange: a deletion has retyped the owner's current page but has not committed.
        using var storage = TornStorage.Create(poolCapacity: 3, durability: StorageCommitDurability.None);
        var original = storage.Insert("original");
        using var deleting = storage.BeginTransaction();
        storage.Delete(deleting, original.PageId, original.SlotIndex);
        using var stale = storage.BeginTransaction();
        using var replacement = storage.BeginTransaction();
        using var resume = new ManualResetEventSlim();
        var pinned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int frees = 0;
        int reuses = 0;
        storage.PagePinnedForWrite = pageId =>
        {
            pageId.ShouldBe(original.PageId);
            storage.PagePinnedForWrite = null;
            pinned.SetResult();
            resume.Wait(cancellationToken);
        };
        storage.FreeSpaceMap.Freed = pageId =>
        {
            pageId.ShouldBe(original.PageId);
            storage.IsPageWriteLocked(pageId).ShouldBeFalse();
            frees++;
        };

        // Act: the stale writer pins first; this thread commits and optionally reallocates
        // before allowing it to register its touch. The replacement keeps its write lock.
        var writer = Task.Run(() =>
        {
            if (insert)
            {
                return ((PageId PageId, int SlotIndex)?)storage.Insert(stale, "new row");
            }

            Should.Throw<StorageIOException>(() => storage.OpenPageForWrite(stale, original.PageId))
                .Message.ShouldBe($"Page {(long)original.PageId} is not allocated.");
            return null;
        }, CancellationToken.None);
        (PageId PageId, int SlotIndex)? inserted;
        try
        {
            await pinned.Task.WaitAsync(cancellationToken);
            deleting.Commit();
            if (reuse)
            {
                await Task.Run(() =>
                {
                    storage.Insert(replacement, 99, [7]).PageId.ShouldBe(original.PageId);
                    reuses++;
                }, CancellationToken.None).WaitAsync(cancellationToken);
            }
        }
        finally
        {
            storage.PagePinnedForWrite = null;
            storage.FreeSpaceMap.Freed = null;
            resume.Set();
            inserted = await writer;
        }

        // Assert: no pre-image or lock was taken on the stale allocation. An insert instead
        // makes a fresh allocation (which may legitimately recycle the same id again).
        frees.ShouldBe(1);
        reuses.ShouldBe(reuse ? 1 : 0);
        stale.PreImages.Count.ShouldBe(insert ? 1 : 0);
        if (reuse)
        {
            stale.PreImages.ContainsKey((long)original.PageId).ShouldBeFalse();
            storage.Update(replacement, original.PageId, 0, new byte[] { 8 });
        }
        else if (!insert)
        {
            storage.IsPageWriteLocked(original.PageId).ShouldBeFalse();
        }

        storage.BufferPool.TryGet(original.PageId, out var pin).ShouldBeTrue();
        using (pin)
        {
            pin.ShouldNotBeNull();
            pin.PinCount.ShouldBe(1);
        }

        switch (completion)
        {
            case 0: stale.Commit(); break;
            case 1: stale.Rollback(); break;
            case 2: stale.Dispose(); break;
            default: throw new ArgumentOutOfRangeException(nameof(completion));
        }
        replacement.Commit();
        storage.ActiveTransactions.ShouldBe(0);
        storage.PreImageBytes.ShouldBe(0);
        storage.IsPageWriteLocked(original.PageId).ShouldBeFalse();
        if (inserted is { } location)
        {
            storage.IsPageWriteLocked(location.PageId).ShouldBeFalse();
            storage.ScanOwner(0).Count().ShouldBe(completion == 0 ? 1 : 0);
        }
        if (reuse)
        {
            storage.ReadBytes(original.PageId, 0).ShouldBe(new byte[] { 8 });
        }
        storage.BufferPool.CheckInvariants();

        // Recovery must preserve the same owners and records before a checkpoint:
        // a stale pre-image must never become the base of the new allocation's WAL.
        using var recovered = TornStorage.Open(storage.CaptureDurable(), poolCapacity: 3);
        recovered.ScanOwner(0).Count().ShouldBe(insert && completion == 0 ? 1 : 0);
        if (reuse)
        {
            recovered.ScanOwner(99).Single().ShouldBe(new byte[] { 8 });
        }
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Stale touch: exceptions and cancellation between pin and lock release the pin and leave no bracket state")]
    [InlineData(false)]
    [InlineData(true)]
    public void OpenPageForWrite_InterruptedBeforeRegistration_ShouldReleasePin(bool cancel)
    {
        // Arrange
        using var storage = TornStorage.Create(poolCapacity: 2);
        var row = storage.Insert("row");
        using var transaction = storage.BeginTransaction();
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        storage.PagePinnedForWrite = _ =>
        {
            if (cancel) { stop.Token.ThrowIfCancellationRequested(); }
            throw new InvalidOperationException("Injected failure before registration.");
        };

        // Act
        try
        {
            if (cancel)
            {
                Should.Throw<OperationCanceledException>(() => storage.OpenPageForWrite(transaction, row.PageId));
            }
            else
            {
                Should.Throw<InvalidOperationException>(() => storage.OpenPageForWrite(transaction, row.PageId));
            }
        }
        finally
        {
            storage.PagePinnedForWrite = null;
        }

        // Assert
        transaction.PreImages.Count.ShouldBe(0);
        storage.IsPageWriteLocked(row.PageId).ShouldBeFalse();
        using (var pin = storage.PageManager.GetPage(row.PageId))
        {
            pin.PinCount.ShouldBe(1);
        }
        storage.Update(transaction, row.PageId, row.SlotIndex, "updated");
        transaction.Commit();
        storage.Read(row.PageId, row.SlotIndex).ShouldBe("updated");
        storage.BufferPool.CheckInvariants();
    }
}
